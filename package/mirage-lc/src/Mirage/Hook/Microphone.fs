module Mirage.Hook.Microphone

open Dissonance
open Dissonance.Audio.Capture
open System
open System.Collections.Concurrent
open System.Buffers
open System.Threading
open Silero.API
open FSharp.Control.Tasks.Affine.Unsafe
open NAudio.Wave
open Mirage.Core.Audio.PCM
open Mirage.Core.Audio.Microphone.Resampler
open Mirage.Core.Audio.Microphone.Detection
open Mirage.Core.Audio.Microphone.Recorder
open Mirage.Core.Ply.Channel
open Mirage.Core.Ply.Fork
open Mirage.Domain.Config
open Mirage.Domain.Setting

let [<Literal>] SamplesPerWindow = 2048
let [<Literal>] StartThreshold = 0.5f
let [<Literal>] EndThreshold = 0.2f

[<Struct>]
type ProcessingInput =
    {   samples: Samples
        format: WaveFormat
        isReady: bool
        isPlayerDead: bool
        pushToTalkEnabled: bool
        isMuted: bool
        allowRecordVoice: bool
    }

[<Struct>]
type ProcessingState =
    {   forcedProbability: ValueOption<float32>
        allowRecordVoice: bool
    }

let mutable private isReady = false

/// A channel for pulling microphone data from __bufferChannel__ into a background thread to process.
let private processingChannel = Channel()

[<Struct>]
type BufferInput =
    {   samples: Samples
        sampleRate: int
        sampleCount: int
        channels: int
    }

let private bufferPool: ArrayPool<float32> = ArrayPool.Create()

/// A channel for pulling microphone data from the dissonance thread.
/// The given audio samples points to an array that belongs to the buffer pool, but we cannot hold this reference for long.
/// A deep copy of the audio samples is done, and then the input audio samples is returned to the buffer pool.
/// Audio data is then sent to __processingChannel__, where the audio samples are safe to use and will not be mutated.
let private bufferChannel =
    let channel = Channel()
    let rec consumer () =
        uply {
            let! input = readChannel' channel
            let samples = Array.zeroCreate<float32> input.sampleCount
            Buffer.BlockCopy(input.samples, 0, samples, 0, input.sampleCount * sizeof<float32>)
            bufferPool.Return(input.samples, true)
            if not (isNull StartOfRound.Instance) then
                writeChannel processingChannel << ValueSome <|
                    {   samples = samples
                        format = WaveFormat(input.sampleRate, input.channels)
                        isReady = isReady
                        isPlayerDead = StartOfRound.Instance.localPlayerController.isPlayerDead
                        pushToTalkEnabled = IngamePlayerSettings.Instance.settings.pushToTalk
                        isMuted = getDissonance().IsMuted
                        allowRecordVoice = getSettings().allowRecordVoice
                    }
            do! consumer()
        }
    fork' consumer
    channel

type MicrophoneSubscriber() =
    interface IMicrophoneSubscriber with
        member _.ReceiveMicrophoneData(buffer, format) =
            let samples = bufferPool.Rent buffer.Count
            Buffer.BlockCopy(buffer.Array, buffer.Offset, samples, 0, buffer.Count * sizeof<float32>)
            writeChannel bufferChannel
                {   samples = samples
                    sampleRate = format.SampleRate
                    sampleCount = buffer.Count
                    channels = format.Channels
                }
        member _.Reset() = writeChannel processingChannel ValueNone

let readMicrophone recordingDirectory =
    let silero = SileroVAD SamplesPerWindow
    let recorder =
        Recorder
            {   minAudioDurationMs = localConfig.MinAudioDurationMs.Value
                directory = recordingDirectory
                allowRecordVoice = _.allowRecordVoice
            }
    let voiceDetector =
        VoiceDetector
            {   minSilenceDurationMs = localConfig.MinSilenceDurationMs.Value
                forcedProbability = _.forcedProbability
                startThreshold = StartThreshold 
                endThreshold = EndThreshold
                detectSpeech = detectSpeech silero
                onVoiceDetected = fun state action -> writeRecorder recorder struct (state, action)
            }
    let resampler = Resampler SamplesPerWindow (writeDetector voiceDetector)
    let rec consumer () =
        uply {
            let! value = readChannel' processingChannel
            match value with
                | ValueNone -> writeResampler resampler ResamplerInput.Reset
                | ValueSome state ->
                    if state.isReady && (getConfig().enableRecordVoiceWhileDead || not state.isPlayerDead) then
                        let frame =
                            {   samples = state.samples
                                format = state.format
                            }
                        let processingState =
                            {   forcedProbability =
                                    if state.isMuted then ValueSome 0f
                                    else if state.pushToTalkEnabled then ValueSome 1f
                                    else ValueNone
                                allowRecordVoice = getSettings().allowRecordVoice
                            }
                        writeResampler resampler <| ResamplerInput struct (processingState, frame)
            do! consumer()
        }
    fork' consumer

    On.Dissonance.DissonanceComms.add_Start(fun orig self ->
        orig.Invoke self
        self.SubscribeToRecordedAudio <| MicrophoneSubscriber()
    )

    // Normally during the opening doors sequence, the game suffers from dropped audio frames, causing recordings to sound glitchy.
    // To reduce the likelihood of recording glitched sounds, audio recordings only start after the sequence is completely finished.
    On.StartOfRound.add_StartTrackingAllPlayerVoices(fun orig self ->
        isReady <- true
        orig.Invoke self
    )

    // Set isReady: false when exiting to the main menu, or the round is over.
    On.StartOfRound.add_OnDestroy(fun orig self ->
        isReady <- false
        orig.Invoke self
    )
    On.StartOfRound.add_ReviveDeadPlayers(fun orig self ->
        isReady <- false
        orig.Invoke self
    )

    On.MenuManager.add_Awake(fun orig self ->
        orig.Invoke self
        writeChannel processingChannel ValueNone
    )