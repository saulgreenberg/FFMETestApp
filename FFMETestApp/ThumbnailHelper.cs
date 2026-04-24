using FFmpeg.AutoGen;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IOPath = System.IO.Path;

namespace FFMETestApp;

// Extracts a single video frame as a scaled WPF BitmapSource using the FFmpeg C API directly
// (via FFmpeg.AutoGen). This bypasses FFME entirely — FFME is a playback engine, not a frame
// extractor. The class is 'unsafe' because the FFmpeg C API uses raw pointers throughout.
internal static unsafe class ThumbnailHelper
{
    // Guards against setting RootPath more than once (it's a global FFmpeg.AutoGen setting).
    private static bool _pathSet;

    // Tell FFmpeg.AutoGen where to find the native FFmpeg DLLs.
    // Must be called before any ffmpeg.* function, which is why every public entry point calls it.
    // Uses the same 'ffmpegbin' folder that FFME already loaded the DLLs from, so no extra copies needed.
    private static void EnsureFFmpegPath()
    {
        if (_pathSet) return;
        _pathSet = true;
        ffmpeg.RootPath = IOPath.Combine(AppContext.BaseDirectory, "ffmpegbin");
    }

    // Decode one frame near seekSeconds into filePath and return a frozen BitmapSource
    // scaled so its height == targetHeight. Returns null on any failure.
    public static BitmapSource? CaptureFrame(string filePath, double seekSeconds, int targetHeight)
    {
        EnsureFFmpegPath();

        // All FFmpeg objects are raw C pointers. They must be explicitly freed in the finally block;
        // the GC has no knowledge of them.
        AVFormatContext* fmt = null;      // container (file wrapper — mp4, mkv, etc.)
        AVCodecContext* codecCtx = null;  // decoder state machine for one stream
        AVFrame* frame = null;            // decoded raw frame (YUV or similar)
        AVFrame* scaled = null;           // converted/scaled frame (BGR24, ready for WPF)
        AVPacket* pkt = null;             // compressed packet read from the container
        SwsContext* sws = null;           // libswscale context: converts pixel format and scales

        try
        {
            // --- Open the container ---
            // avformat_open_input reads the file header and detects the container format (mp4, mkv, …).
            // On success, *fmt is allocated and must later be released with avformat_close_input.
            if (ffmpeg.avformat_open_input(&fmt, filePath, null, null) < 0)
                return null;

            // avformat_find_stream_info reads a few packets to fill in stream metadata
            // (codec, resolution, frame rate, etc.) that the header alone may not contain.
            if (ffmpeg.avformat_find_stream_info(fmt, null) < 0)
                return null;

            // --- Find the first video stream ---
            // A container can hold multiple streams (video, audio, subtitles).
            // We want the first stream whose codec type is video.
            int si = -1;
            for (int i = 0; i < (int)fmt->nb_streams; i++)
            {
                if (fmt->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                {
                    si = i;
                    break;
                }
            }
            if (si < 0) return null;  // no video stream found

            var stream = fmt->streams[si];
            var codecpar = stream->codecpar;  // codec parameters: codec ID, resolution, pixel format, …

            // --- Open the decoder ---
            // avcodec_find_decoder looks up the built-in software decoder for this codec (H.264, H.265, …).
            var codec = ffmpeg.avcodec_find_decoder(codecpar->codec_id);
            if (codec == null) return null;

            // avcodec_alloc_context3 allocates a decoder context and pre-fills it with codec defaults.
            codecCtx = ffmpeg.avcodec_alloc_context3(codec);
            if (codecCtx == null) return null;

            // Copy the stream's codec parameters (resolution, pixel format, extradata, …) into the context.
            if (ffmpeg.avcodec_parameters_to_context(codecCtx, codecpar) < 0) return null;

            // avcodec_open2 finalises the decoder context and makes it ready to receive packets.
            if (ffmpeg.avcodec_open2(codecCtx, codec, null) < 0) return null;

            // --- Seek to near the requested time ---
            // av_seek_frame moves the read position in the container.
            //   stream index -1  → use the internal global clock (AV_TIME_BASE units = microseconds).
            //   AVSEEK_FLAG_BACKWARD → land on the nearest keyframe at or before the target time.
            //     A keyframe (I-frame) is required to start decoding; you cannot decode mid-GOP.
            long seekPts = (long)(seekSeconds * ffmpeg.AV_TIME_BASE);
            ffmpeg.av_seek_frame(fmt, -1, seekPts, ffmpeg.AVSEEK_FLAG_BACKWARD);

            // avcodec_flush_buffers discards any frames the decoder was holding internally
            // after the seek, preventing stale data from polluting the new decode position.
            ffmpeg.avcodec_flush_buffers(codecCtx);

            // Allocate the raw frame buffer and a reusable packet struct.
            frame = ffmpeg.av_frame_alloc();
            pkt = ffmpeg.av_packet_alloc();

            bool got = false;
            int attempts = 600;  // safety cap — avoids an infinite loop on corrupt files

            // --- Decode packets until we reach the target frame ---
            // av_read_frame reads one compressed packet from the container at a time.
            // We decode it, check the frame's presentation timestamp, and stop once we've
            // passed the target time. This is necessary because the seek landed on a keyframe
            // which may be several frames before the exact requested position.
            while (!got && attempts-- > 0 && ffmpeg.av_read_frame(fmt, pkt) >= 0)
            {
                if (pkt->stream_index == si)  // skip packets from audio/subtitle streams
                {
                    // avcodec_send_packet pushes a compressed packet into the decoder.
                    // avcodec_receive_frame pulls out a decoded raw frame (may need several packets).
                    if (ffmpeg.avcodec_send_packet(codecCtx, pkt) == 0 &&
                        ffmpeg.avcodec_receive_frame(codecCtx, frame) == 0)
                    {
                        // best_effort_timestamp is the most reliable PTS estimate FFmpeg can provide.
                        // Multiply by the stream's time_base (a rational number, e.g. 1/90000) to get seconds.
                        double pts = frame->best_effort_timestamp * ffmpeg.av_q2d(stream->time_base);

                        // 0.1 s tolerance: accept the first frame that falls within 100 ms before the target,
                        // because exact frame-accurate seeks aren't always possible with all codecs/containers.
                        if (pts >= seekSeconds - 0.1)
                            got = true;
                    }
                }

                // av_packet_unref releases the packet's internal data buffer so it can be reused.
                ffmpeg.av_packet_unref(pkt);
            }

            if (!got || frame->width <= 0 || frame->height <= 0)
                return null;

            // --- Scale the frame to the requested height ---
            // Compute output width that preserves the original aspect ratio.
            int dstH = targetHeight;
            int dstW = Math.Max(1, (int)Math.Round((double)frame->width / frame->height * dstH));

            // sws_getContext creates a libswscale conversion context.
            // It is configured to convert from the decoded frame's native pixel format (typically YUV420P)
            // to BGR24 (the format WPF's Bgr24 PixelFormat expects), while also rescaling to dstW × dstH.
            // SWS_BILINEAR gives reasonable quality at low cost.
            sws = ffmpeg.sws_getContext(
                frame->width, frame->height, (AVPixelFormat)frame->format,  // source
                dstW, dstH, AVPixelFormat.AV_PIX_FMT_BGR24,                 // destination
                (int)SwsFlags.SWS_BILINEAR, null, null, null);
            if (sws == null) return null;

            // Allocate the destination frame and let FFmpeg compute its buffer requirements.
            scaled = ffmpeg.av_frame_alloc();
            scaled->width = dstW;
            scaled->height = dstH;
            scaled->format = (int)AVPixelFormat.AV_PIX_FMT_BGR24;

            // av_frame_get_buffer allocates the raw pixel buffer inside 'scaled'.
            // align=1 means no padding between rows (simplifies the managed copy below).
            if (ffmpeg.av_frame_get_buffer(scaled, 1) < 0) return null;

            // sws_scale_frame performs both the pixel format conversion and the resize in one call,
            // reading from 'frame' and writing into 'scaled'.
            if (ffmpeg.sws_scale_frame(sws, scaled, frame) < 0) return null;

            // --- Copy pixel data to managed memory ---
            // scaled->linesize[0] is the byte width of one row in the output buffer (may include padding).
            // scaled->data[0] is a pointer to the first pixel row.
            int stride = scaled->linesize[0];
            int bufLen = stride * dstH;
            var buf = new byte[bufLen];

            // Pin the managed array and use Buffer.MemoryCopy for a fast unmanaged-to-managed copy.
            fixed (byte* pBuf = buf)
                Buffer.MemoryCopy(scaled->data[0], pBuf, bufLen, bufLen);

            // --- Create a WPF BitmapSource ---
            // BitmapSource.Create wraps the managed byte array without copying it again.
            // 96 DPI is the WPF default; Bgr24 matches the BGR24 pixel layout we produced above.
            var bmp = BitmapSource.Create(
                dstW, dstH, 96, 96,
                PixelFormats.Bgr24, null,
                buf, stride);

            // Freeze makes the BitmapSource immutable and thread-safe, allowing it to be
            // passed to the UI thread without marshalling the underlying pixel data.
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
        finally
        {
            // Every FFmpeg object must be explicitly freed via its own free function.
            // The double-pointer (&x) form both frees the object and nulls the pointer,
            // matching the FFmpeg C API convention and preventing double-free bugs.
            if (pkt != null) ffmpeg.av_packet_free(&pkt);
            if (frame != null) ffmpeg.av_frame_free(&frame);
            if (scaled != null) ffmpeg.av_frame_free(&scaled);
            if (sws != null) ffmpeg.sws_freeContext(sws);           // sws has no double-pointer variant
            if (codecCtx != null) ffmpeg.avcodec_free_context(&codecCtx);
            if (fmt != null) ffmpeg.avformat_close_input(&fmt);
        }
    }
}
