module Mirage.Domain.Audio.Microphone

open System
open Silero.API
open FSharpPlus
open Whisper.API
open Predictor.Domain
open UnityEngine
open Mirage.Domain.Logger
open Mirage.Unity.Predictor
open Mirage.Core.Audio.Microphone.Resampler
open Mirage.Core.Audio.Microphone.Detection
open Mirage.Core.Audio.Microphone.Recorder
open Mirage.Core.Audio.Microphone.Recognition
open Mirage.Core.Audio.PCM
open Mirage.Core.Async.LVar
open Mirage.Core.Audio.File.Mp3Writer

type RequestStart =
    {   fileId: Guid
        playerId: uint64
        sentenceId: Guid
    }

type RequestEnd =
    {   sentenceId: Guid
        vadTimings: list<VADFrame>
    }

type RequestFound =
    {   vadFrame: VADFrame
        playerId: uint64
        sentenceId: Guid
        samples: Samples 
    }

/// A request to transcribe via the host (from a non-host).
type RequestAction
    = RequestStart of RequestStart
    | RequestEnd of RequestEnd
    | RequestFound of RequestFound

type ResponseFound =
    {   fileId: Guid
        sentenceId: Guid
        vadFrame: VADFrame
        transcription: Transcription
    }

type ResponseEnd =
    {   fileId: Guid
        sentenceId: Guid
        transcription: Transcription
        vadTimings: list<VADFrame>
    }

/// A response containing the transcribed text from the host (to a non-host).
type ResponseAction
    = ResponseFound of ResponseFound
    | ResponseEnd of ResponseEnd

let onTranscribe whisperTimingsVar sentenceId (action: TranscribeLocalAction<Transcription>) =
    logInfo "onTranscribe: before async"
    Async.StartImmediate <|
        async {
            logInfo "onTranscribe: inside async"
            match action with
                | TranscribeStart ->
                    logInfo "Transcription start"
                    Predictor.LocalPlayer.Register <|
                        VoiceActivityAtom
                            {   speakerId = Int StartOfRound.Instance.localPlayerController.playerSteamId
                                prob = 1.0
                                distanceToSpeaker = 0f
                            }
                    logInfo "Transcription start finished"
                | TranscribeEnd payload ->
                    logInfo $"Transcription end. text: {payload.transcription.text}"
                    let toVADTiming vadFrame =
                        let voiceActivityAtom =
                            {   speakerId = Predictor.LocalPlayer.SpeakerId
                                prob = float vadFrame.probability
                                distanceToSpeaker = 0f
                            }
                        (vadFrame.elapsedTime, voiceActivityAtom)
                    let! enemies = accessLVar Predictor.Enemies List.ofSeq
                    let heardAtom =
                        {   text = payload.transcription.text
                            speakerClass = Predictor.LocalPlayer.SpeakerId
                            speakerId = Predictor.LocalPlayer.SpeakerId
                            sentenceId = sentenceId
                            elapsedMillis = payload.vadFrame.elapsedTime
                            transcriptionProb = float payload.transcription.avgLogProb
                            nospeechProb = float payload.transcription.noSpeechProb
                            distanceToSpeaker = 0f
                        }
                    let localPosition = StartOfRound.Instance.localPlayerController.transform.position
                    flip iter enemies <| fun enemy ->
                        enemy.Register << HeardAtom <|
                            {   heardAtom with
                                    distanceToSpeaker = Vector3.Distance(localPosition, enemy.transform.position)
                            }
                    let! whisperTimings = readLVar whisperTimingsVar
                    Predictor.LocalPlayer.Register <|
                        SpokeRecordingAtom
                            {   spokeAtom =
                                    {   text = payload.transcription.text
                                        sentenceId = sentenceId
                                        elapsedMillis = payload.vadFrame.elapsedTime
                                        transcriptionProb = float payload.transcription.avgLogProb
                                        nospeechProb = float payload.transcription.noSpeechProb
                                    }
                                whisperTimings = List.rev whisperTimings
                                vadTimings = toVADTiming <!> payload.vadTimings
                                audioInfo =
                                    {   fileId = payload.fileId
                                        duration = payload.vadFrame.elapsedTime
                                    }
                            }
                    do! modifyLVar whisperTimingsVar <| konst []
                | TranscribeFound payload ->
                    logInfo $"Transcription found. text: {payload.transcription.text}"
                    let! enemies = accessLVar Predictor.Enemies List.ofSeq
                    let spokeAtom =
                        {   text = payload.transcription.text
                            sentenceId = sentenceId
                            transcriptionProb = float payload.transcription.avgLogProb
                            nospeechProb = float payload.transcription.noSpeechProb
                            elapsedMillis = payload.vadFrame.elapsedTime
                        }
                    do! modifyLVar whisperTimingsVar <| List.cons (payload.vadFrame.elapsedTime, spokeAtom)
                    Predictor.LocalPlayer.Register <| SpokeAtom spokeAtom
                    let localPosition = StartOfRound.Instance.localPlayerController.transform.position
                    let heardAtom =
                        {   text = payload.transcription.text
                            speakerClass = Predictor.LocalPlayer.SpeakerId
                            speakerId = Predictor.LocalPlayer.SpeakerId
                            sentenceId = sentenceId
                            elapsedMillis = payload.vadFrame.elapsedTime
                            transcriptionProb = float payload.transcription.avgLogProb
                            nospeechProb = float payload.transcription.noSpeechProb
                            distanceToSpeaker = 0f
                        }
                    flip iter enemies <| fun enemy ->
                        enemy.Register << HeardAtom <|
                            {   heardAtom with
                                    distanceToSpeaker = Vector3.Distance(localPosition, enemy.transform.position)
                            }
        }

type InitMicrophoneProcessor =
    {   recordingDirectory: string
        cudaAvailable: bool
        whisper: Whisper
        silero: SileroVAD
        /// Whether or not we are ready to process audio samples from the microphone.
        isReady: LVar<bool>
        /// Whether or not we should transcribe audio via the host instead of locally.
        transcribeViaHost: LVar<bool>
        /// A function that sends a request action to the host.
        sendRequest: RequestAction -> Async<unit>
        /// A function that sends a response to the target player.
        sendResponse: uint64 -> ResponseAction -> Async<unit>
    }

type MicrophoneProcessor =
    private
        {   resampler: Resampler
            transcriber: VoiceTranscriber<uint64>
        }

let MicrophoneProcessor param =
    let transcribeAudio(request: TranscribeRequest) =
        async {
            logInfo "MicrophoneProcessor transcribeAudio start"
            let! x = 
                transcribe param.whisper
                    {   samplesBatch = toPCMBytes <!> request.samplesBatch
                        language = request.language
                    }
            logInfo $"MicrophoneProcessor transcribeAudio end. Length: {x.Length}. Text (first): {x[0].text}"
            return x
        }

    let transcriber =
        let mutable sentenceId = Guid.NewGuid()
        let whisperTimings = newLVar []
        VoiceTranscriber<uint64, Transcription> transcribeAudio <| fun action ->
            async {
                match action with
                    | TranscribeBatchedAction sentenceAction ->
                        match sentenceAction with
                            | TranscribeBatchedFound payload ->
                                logInfo $"sendResponse: TranscribeBatchedFound. Text: {payload.transcription.text}"
                                do! param.sendResponse payload.playerId << ResponseFound <|
                                    {   fileId = payload.fileId
                                        sentenceId = payload.sentenceId
                                        vadFrame = payload.vadFrame
                                        transcription = payload.transcription
                                    }
                            | TranscribeBatchedEnd payload ->
                                logInfo $"sendResponse: TranscribeBatchedEnd. Text: {payload.transcription.text}"
                                do! param.sendResponse payload.playerId << ResponseEnd <|
                                    {   fileId = payload.fileId
                                        sentenceId = payload.sentenceId
                                        transcription = payload.transcription
                                        vadTimings = payload.vadTimings
                                    }
                    | TranscribeLocalAction transcribeAction ->
                        if transcribeAction = TranscribeStart then
                            sentenceId <- Guid.NewGuid()
                        onTranscribe whisperTimings sentenceId transcribeAction
            }

    let recorder =
        let mutable sentenceId = Guid.NewGuid()
        Recorder param.recordingDirectory <| fun action ->
            async {
                let! transcribeViaHost = readLVar param.transcribeViaHost
                if StartOfRound.Instance.IsHost || not transcribeViaHost then
                    do! writeTranscriber transcriber <| TranscribeLocal action
                else if transcribeViaHost then
                    logInfo "transcribing via host"
                    let playerId = StartOfRound.Instance.localPlayerController.playerClientId
                    match action with
                        | RecordStart payload ->
                            logInfo $"sendRequest. FileId: {getFileId payload.mp3Writer}"
                            sentenceId <- Guid.NewGuid()
                            logInfo $"RecordStart. SentenceId: {sentenceId}"
                            do! param.sendRequest << RequestStart <|
                                {   fileId = getFileId payload.mp3Writer
                                    playerId = playerId
                                    sentenceId = sentenceId
                                }
                        | RecordEnd payload ->
                            logInfo $"RecordEnd. SentenceId: {sentenceId}"
                            do! param.sendRequest << RequestEnd <|
                                {   sentenceId = sentenceId
                                    vadTimings = payload.vadTimings
                                }
                        | RecordFound payload ->
                            logInfo $"RecordFound. SentenceId: {sentenceId}"
                            do! param.sendRequest << RequestFound <|
                                {   vadFrame = payload.vadFrame
                                    playerId = playerId
                                    sentenceId = sentenceId
                                    samples = payload.currentAudio.resampled.samples
                                }
            }
    let detector = VoiceDetector (result << detectSpeech param.silero) <| fun action ->
        async {
            let! isReady = readLVar param.isReady
            if isReady then
                do! writeTranscriber transcriber TryTranscribeAudio
                do! writeRecorder recorder action
        }
    {   resampler = Resampler <| writeDetector detector
        transcriber = transcriber
    }

/// Feed an audio frame from the microphone to be processed.
let processMicrophone processor = writeResampler processor.resampler

let processTranscriber processor = writeTranscriber processor.transcriber