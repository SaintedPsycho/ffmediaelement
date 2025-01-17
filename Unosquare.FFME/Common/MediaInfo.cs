﻿// Ignore Spelling: TBR TBC TBN Unosquare FFME

namespace Unosquare.FFME.Common
{
    using Container;
    using FFmpeg.AutoGen;
    using System;
    using System.Collections.Generic;
    using System.Linq;

    /// <summary>
    /// Holds media information about the input, its chapters, programs and individual stream components.
    /// </summary>
    public unsafe class MediaInfo
    {
        #region Constructor and Initialization

        /// <summary>
        /// Initializes a new instance of the <see cref="MediaInfo"/> class.
        /// </summary>
        /// <param name="container">The container.</param>
        internal MediaInfo(MediaContainer container)
        {
            // The below logic was implemented using the same ideas conveyed by the following code:
            // Reference: https://ffmpeg.org/doxygen/3.2/dump_8c_source.html --
            var ic = container.InputContext;
            MediaSource = container.MediaSource;
            Format = Utilities.PtrToStringUTF8(ic->iformat->name);
            Metadata = container.Metadata;
            StartTime = ic->start_time != ffmpeg.AV_NOPTS_VALUE ? ic->start_time.ToTimeSpan() : TimeSpan.MinValue;
            Duration = ic->duration != ffmpeg.AV_NOPTS_VALUE ? ic->duration.ToTimeSpan() : TimeSpan.MinValue;
            BitRate = ic->bit_rate < 0 ? 0 : ic->bit_rate;

            Streams = ExtractStreams(ic).ToDictionary(k => k.StreamIndex, v => v);
            Chapters = ExtractChapters(ic);
            Programs = ExtractPrograms(ic, Streams);
            BestStreams = FindBestStreams(ic, Streams);
        }

        #endregion

        #region Properties

        /// <summary>
        /// Gets the input URL string used to access and create the media container.
        /// </summary>
        public string MediaSource { get; }

        /// <summary>
        /// Gets the name of the container format.
        /// </summary>
        public string Format { get; }

        /// <summary>
        /// Gets the metadata for the input. This may include stuff like title, creation date, company name, etc.
        /// Individual stream components, chapters and programs may contain additional metadata.
        /// </summary>
        public IReadOnlyDictionary<string, string> Metadata { get; }

        /// <summary>
        /// Gets the duration of the input as reported by the container format.
        /// Individual stream components may have different values.
        /// Returns TimeSpan.MinValue if unknown.
        /// </summary>
        public TimeSpan Duration { get; }

        /// <summary>
        /// Gets the start timestamp of the input as reported by the container format.
        /// Individual stream components may have different values.
        /// Returns TimeSpan.MinValue if unknown.
        /// </summary>
        public TimeSpan StartTime { get; }

        /// <summary>
        /// If available, returns a non-zero value as reported by the container format.
        /// </summary>
        public long BitRate { get; }

        /// <summary>
        /// Gets a list of chapters.
        /// </summary>
        public IReadOnlyList<ChapterInfo> Chapters { get; }

        /// <summary>
        /// Gets a list of programs with their associated streams.
        /// </summary>
        public IReadOnlyList<ProgramInfo> Programs { get; }

        /// <summary>
        /// Gets the dictionary of stream information components by stream index.
        /// </summary>
        public IReadOnlyDictionary<int, StreamInfo> Streams { get; }

        /// <summary>
        /// Provides access to the best streams of each media type found in the container.
        /// This uses some internal FFmpeg heuristics.
        /// </summary>
        public IReadOnlyDictionary<AVMediaType, StreamInfo> BestStreams { get; }

        #endregion

        #region Methods

        /// <summary>
        /// Extracts the stream infos from the input.
        /// </summary>
        /// <param name="inputContext">The input context.</param>
        /// <returns>The list of stream infos.</returns>
        private static List<StreamInfo> ExtractStreams(AVFormatContext* inputContext)
        {
            var result = new List<StreamInfo>(32);
            if (inputContext == null || inputContext->streams == null)
                return result;

            for (var i = 0; i < inputContext->nb_streams; i++)
            {
                var s = inputContext->streams[i];

                var codecContext = ffmpeg.avcodec_alloc_context3(null);
                ffmpeg.avcodec_parameters_to_context(codecContext, s->codecpar);

                var bitsPerSample = codecContext->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO
                                    ? ffmpeg.av_get_bits_per_sample(codecContext->codec_id)
                                    : 0;

                var stream = new StreamInfo
                {
                    StreamId = s->id,
                    StreamIndex = s->index,
                    Metadata = FFDictionary.ToDictionary(s->metadata),
                    CodecType = codecContext->codec_type,
                    CodecTypeName = ffmpeg.av_get_media_type_string(codecContext->codec_type),
                    Codec = codecContext->codec_id,
                    CodecName = ffmpeg.avcodec_get_name(codecContext->codec_id),
                    CodecProfile = ffmpeg.avcodec_profile_name(codecContext->codec_id, codecContext->profile),
                    ReferenceFrameCount = codecContext->refs,
                    CodecTag = codecContext->codec_tag,
                    PixelFormat = codecContext->pix_fmt,
                    FieldOrder = codecContext->field_order,
                    IsInterlaced = codecContext->field_order != AVFieldOrder.AV_FIELD_PROGRESSIVE
                                && codecContext->field_order != AVFieldOrder.AV_FIELD_UNKNOWN,
                    ColorRange = codecContext->color_range,
                    PixelWidth = codecContext->width,
                    PixelHeight = codecContext->height,
                    HasClosedCaptions = (codecContext->properties & ffmpeg.FF_CODEC_PROPERTY_CLOSED_CAPTIONS) != 0,
                    IsLossless = (codecContext->properties & ffmpeg.FF_CODEC_PROPERTY_LOSSLESS) != 0,
                    Channels = codecContext->channels,
                    BitRate = bitsPerSample > 0
                                ? bitsPerSample * codecContext->channels * codecContext->sample_rate
                                : codecContext->bit_rate,
                    MaxBitRate = codecContext->rc_max_rate,
                    InfoFrameCount = s->codec_info_nb_frames,
                    TimeBase = s->time_base,
                    SampleFormat = codecContext->sample_fmt,
                    SampleRate = codecContext->sample_rate,
                    DisplayAspectRatio = codecContext->height > 0
                                          ? ffmpeg.av_d2q((double)codecContext->width / codecContext->height, int.MaxValue)
                                          : default,
                    SampleAspectRatio = codecContext->sample_aspect_ratio,
                    Disposition = s->disposition,
                    StartTime = s->start_time.ToTimeSpan(s->time_base),
                    Duration = s->duration.ToTimeSpan(s->time_base),
                    FPS = s->avg_frame_rate.ToDouble(),
                    TBR = s->r_frame_rate.ToDouble(),
                    TBN = 1d / s->time_base.ToDouble(),
                    TBC = 1d / codecContext->time_base.ToDouble()
                };

                // Extract valid hardware configurations
                stream.HardwareDevices = HardwareAccelerator.GetCompatibleDevices(stream.Codec);
                stream.HardwareDecoders = GetHardwareDecoders(stream.Codec);

                // TODO: I chose not to include Side data but I could easily do so
                // https://ffmpeg.org/doxygen/3.2/dump_8c_source.html
                // See function: dump_sidedata
                ffmpeg.avcodec_free_context(&codecContext);

                result.Add(stream);
            }

            return result;
        }

        /// <summary>
        /// Finds the best streams for audio video, and subtitles.
        /// </summary>
        /// <param name="ic">The ic.</param>
        /// <param name="streams">The streams.</param>
        /// <returns>The star infos.</returns>
        private static Dictionary<AVMediaType, StreamInfo> FindBestStreams(AVFormatContext* ic, IReadOnlyDictionary<int, StreamInfo> streams)
        {
            // Initialize and clear all the stream indexes.
            var streamIndexes = new Dictionary<AVMediaType, int>();

            for (var i = 0; i < (int)AVMediaType.AVMEDIA_TYPE_NB; i++)
                streamIndexes[(AVMediaType)i] = -1;

            // Find best streams for each component
            // if we passed null instead of the requestedCodec pointer, then
            // find_best_stream would not validate whether a valid decoder is registered.
            AVCodec* requestedCodec = null;

            streamIndexes[AVMediaType.AVMEDIA_TYPE_VIDEO] =
                ffmpeg.av_find_best_stream(
                    ic,
                    AVMediaType.AVMEDIA_TYPE_VIDEO,
                    streamIndexes[(int)AVMediaType.AVMEDIA_TYPE_VIDEO],
                    -1,
                    &requestedCodec,
                    0);

            streamIndexes[AVMediaType.AVMEDIA_TYPE_AUDIO] =
                ffmpeg.av_find_best_stream(
                    ic,
                    AVMediaType.AVMEDIA_TYPE_AUDIO,
                    streamIndexes[AVMediaType.AVMEDIA_TYPE_AUDIO],
                    streamIndexes[AVMediaType.AVMEDIA_TYPE_VIDEO],
                    &requestedCodec,
                    0);

            streamIndexes[AVMediaType.AVMEDIA_TYPE_SUBTITLE] =
                ffmpeg.av_find_best_stream(
                    ic,
                    AVMediaType.AVMEDIA_TYPE_SUBTITLE,
                    streamIndexes[AVMediaType.AVMEDIA_TYPE_SUBTITLE],
                    streamIndexes[AVMediaType.AVMEDIA_TYPE_AUDIO] >= 0 ?
                        streamIndexes[AVMediaType.AVMEDIA_TYPE_AUDIO] :
                        streamIndexes[AVMediaType.AVMEDIA_TYPE_VIDEO],
                    &requestedCodec,
                    0);

            var result = new Dictionary<AVMediaType, StreamInfo>();
            foreach (var kvp in streamIndexes.Where(n => n.Value >= 0))
            {
                result[kvp.Key] = streams[kvp.Value];
            }

            return result;
        }

        /// <summary>
        /// Extracts the chapters from the input.
        /// </summary>
        /// <param name="ic">The ic.</param>
        /// <returns>The chapters.</returns>
        private static List<ChapterInfo> ExtractChapters(AVFormatContext* ic)
        {
            var result = new List<ChapterInfo>(128);
            if (ic->chapters == null) return result;

            for (var i = 0; i < ic->nb_chapters; i++)
            {
                var c = ic->chapters[i];

                var chapter = new ChapterInfo
                {
                    StartTime = c->start.ToTimeSpan(c->time_base),
                    EndTime = c->end.ToTimeSpan(c->time_base),
                    Index = i,
                    ChapterId = c->id,
                    Metadata = FFDictionary.ToDictionary(c->metadata)
                };

                result.Add(chapter);
            }

            return result;
        }

        /// <summary>
        /// Extracts the programs from the input and creates associations between programs and streams.
        /// </summary>
        /// <param name="ic">The ic.</param>
        /// <param name="streams">The streams.</param>
        /// <returns>The program information.</returns>
        private static List<ProgramInfo> ExtractPrograms(AVFormatContext* ic, IReadOnlyDictionary<int, StreamInfo> streams)
        {
            var result = new List<ProgramInfo>(128);
            if (ic->programs == null) return result;

            for (var i = 0; i < ic->nb_programs; i++)
            {
                var p = ic->programs[i];

                var program = new ProgramInfo
                {
                    Metadata = FFDictionary.ToDictionary(p->metadata),
                    ProgramId = p->id,
                    ProgramNumber = p->program_num
                };

                var associatedStreams = new List<StreamInfo>(32);
                for (var s = 0; s < p->nb_stream_indexes; s++)
                {
                    var streamIndex = Convert.ToInt32(p->stream_index[s]);
                    if (streams.ContainsKey(streamIndex))
                        associatedStreams.Add(streams[streamIndex]);
                }

                program.Streams = associatedStreams;

                result.Add(program);
            }

            return result;
        }

        /// <summary>
        /// Gets the available hardware decoder codecs for the given codec id (codec family).
        /// </summary>
        /// <param name="codecFamily">The codec family.</param>
        /// <returns>A list of hardware-enabled decoder codec names.</returns>
        private static List<string> GetHardwareDecoders(AVCodecID codecFamily)
        {
            var result = new List<string>(16);

            foreach (var c in Library.AllCodecs)
            {
                if (ffmpeg.av_codec_is_decoder(c) == 0)
                    continue;

                if (c->id != codecFamily)
                    continue;

                if ((c->capabilities & ffmpeg.AV_CODEC_CAP_HARDWARE) != 0
                    || (c->capabilities & ffmpeg.AV_CODEC_CAP_HYBRID) != 0)
                {
                    result.Add(Utilities.PtrToStringUTF8(c->name));
                }
            }

            return result;
        }

        #endregion
    }

    /// <summary>
    /// Represents media stream information.
    /// </summary>
    public class StreamInfo
    {
        /// <summary>
        /// Gets the stream identifier. This is different from the stream index.
        /// Typically this value is not very useful.
        /// </summary>
        public int StreamId { get; internal set; }

        /// <summary>
        /// Gets the index of the stream.
        /// </summary>
        public int StreamIndex { get; internal set; }

        /// <summary>
        /// Gets the type of the codec.
        /// </summary>
        public AVMediaType CodecType { get; internal set; }

        /// <summary>
        /// Gets the name of the codec type. Audio, Video, Subtitle, Data, etc.
        /// </summary>
        public string CodecTypeName { get; internal set; }

        /// <summary>
        /// Gets the codec identifier.
        /// </summary>
        public AVCodecID Codec { get; internal set; }

        /// <summary>
        /// Gets the name of the codec.
        /// </summary>
        public string CodecName { get; internal set; }

        /// <summary>
        /// Gets the codec profile. Only valid for H.264 or
        /// video codecs that use profiles. Otherwise empty.
        /// </summary>
        public string CodecProfile { get; internal set; }

        /// <summary>
        /// Gets the codec tag. Not very useful except for fixing bugs with
        /// some demuxer scenarios.
        /// </summary>
        public uint CodecTag { get; internal set; }

        /// <summary>
        /// Gets a value indicating whether this stream has closed captions.
        /// Typically this is set for video streams.
        /// </summary>
        public bool HasClosedCaptions { get; internal set; }

        /// <summary>
        /// Gets a value indicating whether this stream contains lossless compressed data.
        /// </summary>
        public bool IsLossless { get; internal set; }

        /// <summary>
        /// Gets the pixel format. Only valid for Video streams.
        /// </summary>
        public AVPixelFormat PixelFormat { get; internal set; }

        /// <summary>
        /// Gets the width of the video frames.
        /// </summary>
        public int PixelWidth { get; internal set; }

        /// <summary>
        /// Gets the height of the video frames.
        /// </summary>
        public int PixelHeight { get; internal set; }

        /// <summary>
        /// Gets the field order. This is useful to determine
        /// if the video needs de-interlacing.
        /// </summary>
        public AVFieldOrder FieldOrder { get; internal set; }

        /// <summary>
        /// Gets a value indicating whether the video frames are interlaced.
        /// </summary>
        public bool IsInterlaced { get; internal set; }

        /// <summary>
        /// Gets the video color range.
        /// </summary>
        public AVColorRange ColorRange { get; internal set; }

        /// <summary>
        /// Gets the number of audio channels.
        /// </summary>
        public int Channels { get; internal set; }

        /// <summary>
        /// Gets the audio sample rate.
        /// </summary>
        public int SampleRate { get; internal set; }

        /// <summary>
        /// Gets the audio sample format.
        /// </summary>
        public AVSampleFormat SampleFormat { get; internal set; }

        /// <summary>
        /// Gets the stream time base unit in seconds.
        /// </summary>
        public AVRational TimeBase { get; internal set; }

        /// <summary>
        /// Gets the sample aspect ratio.
        /// </summary>
        public AVRational SampleAspectRatio { get; internal set; }

        /// <summary>
        /// Gets the display aspect ratio.
        /// </summary>
        public AVRational DisplayAspectRatio { get; internal set; }

        /// <summary>
        /// Gets the reported bit rate. 9 for unavailable.
        /// </summary>
        public long BitRate { get; internal set; }

        /// <summary>
        /// Gets the maximum bit rate for variable bit rate streams. 0 if unavailable.
        /// </summary>
        public long MaxBitRate { get; internal set; }

        /// <summary>
        /// Gets the number of frames that were read to obtain the stream's information.
        /// </summary>
        public int InfoFrameCount { get; internal set; }

        /// <summary>
        /// Gets the number of reference frames.
        /// </summary>
        public int ReferenceFrameCount { get; internal set; }

        /// <summary>
        /// Gets the average FPS reported by the stream.
        /// </summary>
        public double FPS { get; internal set; }

        /// <summary>
        /// Gets the real (base) frame rate of the stream.
        /// </summary>
        public double TBR { get; internal set; }

        /// <summary>
        /// Gets the fundamental unit of time in 1/seconds used to represent timestamps in the stream, according to the stream data.
        /// </summary>
        public double TBN { get; internal set; }

        /// <summary>
        /// Gets the fundamental unit of time in 1/seconds used to represent timestamps in the stream ,according to the codec.
        /// </summary>
        public double TBC { get; internal set; }

        /// <summary>
        /// Gets the disposition flags.
        /// Please see ffmpeg.AV_DISPOSITION_* fields.
        /// </summary>
        public int Disposition { get; internal set; }

        /// <summary>
        /// Gets the start time.
        /// </summary>
        public TimeSpan StartTime { get; internal set; }

        /// <summary>
        /// Gets the duration.
        /// </summary>
        public TimeSpan Duration { get; internal set; }

        /// <summary>
        /// Gets the stream's metadata.
        /// </summary>
        public IReadOnlyDictionary<string, string> Metadata { get; internal set; }

        /// <summary>
        /// Gets the compatible hardware device configurations for the stream's codec.
        /// </summary>
        public IReadOnlyList<HardwareDeviceInfo> HardwareDevices { get; internal set; }

        /// <summary>
        /// Gets a list of compatible hardware decoder names.
        /// </summary>
        public IReadOnlyList<string> HardwareDecoders { get; internal set; }

        /// <summary>
        /// Gets the language string from the stream's metadata.
        /// </summary>
        public string Language => Metadata.ContainsKey("language") ?
            Metadata["language"] : string.Empty;

        /// <summary>
        /// Gets a value indicating whether the stream contains data that is not considered to be
        /// audio, video, or subtitles.
        /// </summary>
        public bool IsNonMedia =>
            CodecType == AVMediaType.AVMEDIA_TYPE_DATA ||
            CodecType == AVMediaType.AVMEDIA_TYPE_ATTACHMENT ||
            CodecType == AVMediaType.AVMEDIA_TYPE_UNKNOWN;
    }

    /// <summary>
    /// Represents a chapter within a container.
    /// </summary>
    public class ChapterInfo
    {
        /// <summary>
        /// Gets the chapter index.
        /// </summary>
        public int Index { get; internal set; }

        /// <summary>
        /// Gets the chapter identifier.
        /// </summary>
        public int ChapterId { get; internal set; }

        /// <summary>
        /// Gets the start time of the chapter.
        /// </summary>
        public TimeSpan StartTime { get; internal set; }

        /// <summary>
        /// Gets the end time of the chapter.
        /// </summary>
        public TimeSpan EndTime { get; internal set; }

        /// <summary>
        /// Gets the chapter metadata.
        /// </summary>
        public IReadOnlyDictionary<string, string> Metadata { get; internal set; }
    }

    /// <summary>
    /// Represents a program and its associated streams within a container.
    /// </summary>
    public class ProgramInfo
    {
        /// <summary>
        /// Gets the program number.
        /// </summary>
        public int ProgramNumber { get; internal set; }

        /// <summary>
        /// Gets the program identifier.
        /// </summary>
        public int ProgramId { get; internal set; }

        /// <summary>
        /// Gets the program metadata.
        /// </summary>
        public IReadOnlyDictionary<string, string> Metadata { get; internal set; }

        /// <summary>
        /// Gets the associated program streams.
        /// </summary>
        public IReadOnlyList<StreamInfo> Streams { get; internal set; }

        /// <summary>
        /// Gets the name of the program. Empty if unavailable.
        /// </summary>
        public string Name => Metadata.ContainsKey("name") ?
            Metadata["name"] : string.Empty;
    }
}
