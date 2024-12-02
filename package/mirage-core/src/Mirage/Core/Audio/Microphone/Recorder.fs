module Mirage.Core.Audio.Microphone.Recorder

#nowarn "40"

open System
open FSharpPlus
open FSharpx.Control
open NAudio.Wave
open Mirage.Core.Audio.Microphone.Detection
open Mirage.Core.Audio.Microphone.Resampler
open Mirage.Core.Audio.Opus.Writer
open System.IO

type RecordStart =
    {   originalFormat: WaveFormat
        resampledFormat: WaveFormat
    }

type RecordFound =
    {   vadFrame: VADFrame
        fullAudio: ResampledAudio
        currentAudio: ResampledAudio
    }

/// Note: After the callback finishes for this action, the mp3 writer is disposed.
type RecordEnd =
    {   opusWriter: OpusWriter
        vadFrame: VADFrame
        vadTimings: list<VADFrame>
        fullAudio: ResampledAudio
        currentAudio: ResampledAudio
        audioDurationMs: int
    }

/// A sum type representing the progress of a recording.
type RecordAction
    = RecordStart of RecordStart
    | RecordFound of RecordFound
    | RecordEnd of RecordEnd

/// Records audio from a live microphone feed.
type Recorder<'State> =
    private { agent: BlockingQueueAgent<Tuple<'State, DetectAction>> }
    interface IDisposable with
        member this.Dispose() = dispose this.agent

type RecorderArgs<'State> =
    {   /// Minimum amount of audio duration that a recording should contain. If the minimum isn't met, the recording is not written to disk.
        minAudioDurationMs: int
        /// Directory to write recordings to.
        directory: string
        /// Whether recordings should be created or not, based on the current state.
        allowRecordVoice: 'State -> bool
        /// Function that gets called every time a recording is in the process of being created.
        onRecording: 'State -> RecordAction -> Async<Unit>
    }

let Recorder<'State> args =
    let agent = new BlockingQueueAgent<Tuple<'State, DetectAction>>(Int32.MaxValue)
    let rec consumer =
        async {
            let! (state, action) = agent.AsyncGet() 
            let onRecording = args.onRecording state
            match action with
                | DetectStart payload ->
                    do! onRecording << RecordStart <|
                        {   originalFormat = payload.originalFormat
                            resampledFormat = payload.resampledFormat
                        }
                | DetectEnd payload ->
                    if payload.audioDurationMs >= args.minAudioDurationMs && args.allowRecordVoice state then
                        let opusWriter =
                            OpusWriter 
                                {   filePath = Path.Join(args.directory, $"{Guid.NewGuid()}.opus")
                                    format = payload.fullAudio.original.format
                                }
                        do! writeOpusSamples opusWriter payload.fullAudio.original.samples
                        do! closeOpusWriter opusWriter
                        do! onRecording << RecordEnd <|
                            {   opusWriter = opusWriter
                                vadFrame = payload.vadFrame
                                vadTimings = payload.vadTimings
                                fullAudio = payload.fullAudio
                                currentAudio = payload.currentAudio
                                audioDurationMs = payload.audioDurationMs
                            }
                | DetectFound payload ->
                    do! onRecording << RecordFound <|
                        {   vadFrame = payload.vadFrame
                            fullAudio = payload.fullAudio
                            currentAudio = payload.currentAudio
                        }
            do! consumer
        }
    Async.Start consumer
    { agent = agent }

let writeRecorder = _.agent.AsyncAdd