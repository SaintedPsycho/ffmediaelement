﻿namespace Unosquare.FFME.Engine
{
    using Common;
    using Container;
    using Diagnostics;
    using FFmpeg.AutoGen;
    using Primitives;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Runtime.CompilerServices;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Implement frame decoding worker logic.
    /// </summary>
    /// <seealso cref="IMediaWorker" />
    internal sealed class FrameDecodingWorker : IntervalWorkerBase, IMediaWorker, ILoggingSource
    {
        private readonly Action<IEnumerable<MediaType>, CancellationToken> SerialDecodeBlocks;
        private readonly Action<IEnumerable<MediaType>, CancellationToken> ParallelDecodeBlocks;

        /// <summary>
        /// The decoded frame count for a cycle. This is used to detect end of decoding scenarios.
        /// </summary>
        private int DecodedFrameCount;

        /// <summary>
        /// Initializes a new instance of the <see cref="FrameDecodingWorker"/> class.
        /// </summary>
        /// <param name="mediaCore">The media core.</param>
        public FrameDecodingWorker(MediaEngine mediaCore)
            : base(nameof(FrameDecodingWorker))
        {
            MediaCore = mediaCore;
            Container = mediaCore.Container;
            State = mediaCore.State;

            ParallelDecodeBlocks = (all, ct) =>
            {
                Parallel.ForEach(all, (t) =>
                    Interlocked.Add(ref DecodedFrameCount,
                    DecodeComponentBlocks(t, ct)));
            };

            SerialDecodeBlocks = (all, ct) =>
            {
                foreach (var t in Container.Components.MediaTypes)
                    DecodedFrameCount += DecodeComponentBlocks(t, ct);
            };

            Container.Components.OnFrameDecoded = (frame, type) =>
            {
                unsafe
                {
                    if (type == MediaType.Audio)
                        MediaCore.Connector?.OnAudioFrameDecoded((AVFrame*)frame.ToPointer(), Container.InputContext);
                    else if (type == MediaType.Video)
                        MediaCore.Connector?.OnVideoFrameDecoded((AVFrame*)frame.ToPointer(), Container.InputContext);
                }
            };

            Container.Components.OnSubtitleDecoded = (subtitle) =>
            {
                unsafe
                {
                    MediaCore.Connector?.OnSubtitleDecoded((AVSubtitle*)subtitle.ToPointer(), Container.InputContext);
                }
            };
        }

        /// <inheritdoc />
        public MediaEngine MediaCore { get; }

        /// <inheritdoc />
        ILoggingHandler ILoggingSource.LoggingHandler => MediaCore;

        /// <summary>
        /// Gets the Media Engine's Container.
        /// </summary>
        private MediaContainer Container { get; }

        /// <summary>
        /// Gets the Media Engine's State.
        /// </summary>
        private MediaEngineState State { get; }

        /// <summary>
        /// Gets a value indicating whether parallel decoding is enabled.
        /// </summary>
        private bool UseParallelDecoding => MediaCore.Timing.HasDisconnectedClocks || Container.MediaOptions.UseParallelDecoding;

        /// <inheritdoc />
        protected override void ExecuteCycleLogic(CancellationToken ct)
        {
            try
            {
                if (MediaCore.HasDecodingEnded || ct.IsCancellationRequested)
                    return;

                // Call the frame decoding logic
                DecodedFrameCount = 0;
                if (UseParallelDecoding)
                    ParallelDecodeBlocks.Invoke(Container.Components.MediaTypes, ct);
                else
                    SerialDecodeBlocks.Invoke(Container.Components.MediaTypes, ct);
            }
            finally
            {
                // Provide updates to decoding stats -- don't count attached pictures
                var hasAttachedPictures = Container.Components.Video?.IsStillPictures ?? false;
                State.UpdateDecodingStats(MediaCore.Blocks.Values
                    .Sum(b => b.MediaType == MediaType.Video && hasAttachedPictures ? 0 : b.RangeBitRate));

                // Detect End of Decoding Scenarios
                // The Rendering will check for end of media when this condition is set.
                MediaCore.HasDecodingEnded = DetectHasDecodingEnded();
            }
        }

        /// <inheritdoc />
        protected override void OnCycleException(Exception ex) =>
            this.LogError(Aspects.DecodingWorker, "Worker Cycle exception thrown", ex);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int DecodeComponentBlocks(MediaType t, CancellationToken ct)
        {
            var decoderBlocks = MediaCore.Blocks[t]; // the blocks reference
            var addedBlocks = 0; // the number of blocks that have been added
            var maxAddedBlocks = decoderBlocks.Capacity; // the max blocks to add for this cycle

            while (addedBlocks < maxAddedBlocks)
            {
                var position = MediaCore.Timing.GetPosition(t).Ticks;
                var rangeHalf = decoderBlocks.RangeMidTime.Ticks;

                // We break decoding if we have a full set of blocks and if the
                // clock is not past the first half of the available block range
                if (decoderBlocks.IsFull && position < rangeHalf)
                    break;

                // Try adding the next block. Stop decoding upon failure or cancellation
                if (ct.IsCancellationRequested || AddNextBlock(t) == false)
                    break;

                // At this point we notify that we have added the block
                addedBlocks++;
            }

            return addedBlocks;
        }

        /// <summary>
        /// Tries to receive the next frame from the decoder by decoding queued
        /// Packets and converting the decoded frame into a Media Block which gets
        /// queued into the playback block buffer.
        /// </summary>
        /// <param name="t">The MediaType.</param>
        /// <returns>True if a block could be added. False otherwise.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool AddNextBlock(MediaType t)
        {
            // Decode the frames
            var block = MediaCore.Blocks[t].Add(Container.Components[t].ReceiveNextFrame(), Container);
            return block != null;
        }

        /// <summary>
        /// Detects the end of media in the decoding worker.
        /// </summary>
        /// <returns>True if media decoding has ended.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool DetectHasDecodingEnded() =>
            DecodedFrameCount <= 0 &&
            !CanReadMoreFramesOf(Container.Components.SeekableMediaType);

        /// <summary>
        /// Gets a value indicating whether more frames can be decoded into blocks of the given type.
        /// </summary>
        /// <param name="t">The media type.</param>
        /// <returns>
        ///   <c>true</c> if more frames can be decoded; otherwise, <c>false</c>.
        /// </returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool CanReadMoreFramesOf(MediaType t) => Container.Components[t].BufferLength > 0
                                                      || Container.Components[t].HasPacketsInCodec
                                                      || MediaCore.ShouldReadMorePackets;
    }
}
