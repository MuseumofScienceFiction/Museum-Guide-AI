using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Ionic.Zip;
using TMPro;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Networking;
using Vosk;
using Debug = UnityEngine.Debug;

namespace MoSF.Vosk {
	/// <summary>
	/// MoSF Vosk speech-to-text manager
	/// Intended to always be running in the background, but can be stopped or started as-needed
	/// Needs to be explicitly started and stopped via StartListening and StopListening
	/// </summary>
	[RequireComponent(typeof(VoiceProcessor))]
	public class VoskVoiceListener : MonoBehaviour {
		[Header("Model Configuration")]
		[Tooltip("Location of the model, relative to the Streaming Assets folder")]
		public string modelPath = "vosk-model-small-en-us-0.15.zip";

		[Tooltip("Start listening after the model has initialized")]
		public bool startListeningOnStart = true;

		[Tooltip("Max number of alternative phrases Vosk will return per event")]
		public int maxAlternatives = 3;

		[Header("Voice Commands")]
		[Tooltip("List of phrases to detect and the events to trigger when they are spoken.")]
		public List<VoiceCommand> voiceCommands = new List<VoiceCommand>();

		[Tooltip("If true, the spoken text must exactly match the phrase. If false, the event triggers if the spoken text simply CONTAINS the phrase.")]
		public bool requireExactMatch = false;

		[Header("Dependencies")]
		[Tooltip("The source of the microphone input")]
		public VoiceProcessor voiceProcessor;

		[Tooltip("Optional: UI Text element to append recognized phrases to")]
		public TextMeshProUGUI outputText;

		[Header("Global Events")]
		[Tooltip("Fired whenever ANY valid text is recognized. Useful for custom parsing.")]
		public UnityEvent<string> onAnyTextRecognized;

		[Header("Debugging")]
		[Tooltip("If true, measures time from Vosk detecting a word to final output")]
		public bool trackLatency = false;

		// Cached Vosk objects
		private Model model;
		private VoskRecognizer recognizer;
		private bool recognizerReady;

		// State and path tracking
		private string decompressedModelPath;
		private string grammar = "";
		private bool isDecompressing;
		private bool didInit;
		private bool running;

		// Data structure to pass both the text and timing data across threads
		private struct VoskResult {
			public string json;
			public float latency;
		}

		// Thread-safe queues to pass audio data between Unity and the worker thread
		private readonly ConcurrentQueue<short[]> threadedBufferQueue = new ConcurrentQueue<short[]>();
		private readonly ConcurrentQueue<VoskResult> threadedResultQueue = new ConcurrentQueue<VoskResult>();

		// Profiler markers to keep an eye on performance in the editor
		static readonly ProfilerMarker voskRecognizerCreateMarker = new ProfilerMarker("VoskRecognizer.Create");
		static readonly ProfilerMarker voskRecognizerReadMarker = new ProfilerMarker("VoskRecognizer.AcceptWaveform");

		private void Start() {
			// Grab the component if we forgot to assign it in the inspector
			if (voiceProcessor == null) {
				voiceProcessor = GetComponent<VoiceProcessor>();
			}

			// Start loading the model right away so it's ready when the player needs it
			StartCoroutine(InitializeModel());
		}

		private void Update() {
			// Pull transcription results from the worker thread and process them on the main thread
			// Because this runs in Update(), UnityEvents are safely invoked on the main thread!
			if (threadedResultQueue.TryDequeue(out VoskResult result)) {
				ProcessTranscription(result.json, result.latency);
			}
		}

		// Starts grabbing mic input and passes it to the background thread
		public void StartListening() {
			if (!didInit) {
				Debug.LogWarning("VoskVoiceListener: Initialization incomplete. Cannot start listening yet");
				return;
			}

			if (!voiceProcessor.IsRecording) {
				running = true;
				voiceProcessor.StartRecording();

				// Fire and forget the background task
				Task.Run(ThreadedWork).ConfigureAwait(false);
				Debug.Log("VoskVoiceListener: Listening started");
			}
		}

		// Stops mic capture and kills the processing loop
		public void StopListening() {
			if (voiceProcessor.IsRecording) {
				running = false;
				voiceProcessor.StopRecording();
				Debug.Log("VoskVoiceListener: Listening stopped");
			}
		}

		/// <summary>
		/// Allows external, non-Vosk systems (like a text chat, UI button, or another AI) 
		/// to inject text directly into the command processing stream.
		/// </summary>
		/// <param name="text">The raw text to process</param>
		public void InjectRecognizedText(string text) {
			if (string.IsNullOrWhiteSpace(text)) {
				return;
			}

			string normalizedText = text.Trim().ToLower();

			if (normalizedText == "[unk]") {
				return;
			}

			Debug.Log($"Injected Text Recognized: {normalizedText}");

			HandleRecognizedText(normalizedText);
		}

		// Unzips the model from StreamingAssets and loads it into memory
		private IEnumerator InitializeModel() {
			yield return WaitForMicrophoneInput();
			yield return Decompress();

			model = new Model(decompressedModelPath);

			// Wait a frame before hooking up events just to be safe
			yield return null;

			voiceProcessor.OnFrameCaptured += VoiceProcessorOnFrameCaptured;
			voiceProcessor.OnRecordingStop += VoiceProcessorOnRecordingStop;

			didInit = true;
			Debug.Log("VoskVoiceListener: Model initialized successfully");

			// Trigger listening now that the system is fully ready
			if (startListeningOnStart) {
				StartListening();
			}
		}

		// Formats our target phrases into the JSON array Vosk expects
		private void UpdateGrammar() {
			if (voiceCommands == null || voiceCommands.Count == 0) {
				grammar = "";
				return;
			}

			JSONArray keywords = new JSONArray();
			foreach (VoiceCommand cmd in voiceCommands) {
				if (!string.IsNullOrWhiteSpace(cmd.phrase)) {
					keywords.Add(new JSONString(cmd.phrase.ToLower().Trim()));
				}
			}

			// The [unk] token lets Vosk filter out garbage noise or words we don't care about
			keywords.Add(new JSONString("[unk]"));
			grammar = keywords.ToString();
		}

		// Extracts the zip file to persistent data if it hasn't been already
		private IEnumerator Decompress() {
			string targetDirectory = Path.Combine(Application.persistentDataPath, Path.GetFileNameWithoutExtension(modelPath));

			if (!Path.HasExtension(modelPath) || Directory.Exists(targetDirectory)) {
				decompressedModelPath = targetDirectory;
				yield break;
			}

			string dataPath = Path.Combine(Application.streamingAssetsPath, modelPath);
			Stream dataStream;

			if (dataPath.Contains("://")) {
				UnityWebRequest www = UnityWebRequest.Get(dataPath);
				www.SendWebRequest();
				while (!www.isDone) {
					yield return null;
				}
				dataStream = new MemoryStream(www.downloadHandler.data);
			}
			else {
				dataStream = File.OpenRead(dataPath);
			}

			ZipFile zipFile = ZipFile.Read(dataStream);
			zipFile.ExtractProgress += ZipFileOnExtractProgress;
			zipFile.ExtractAll(Application.persistentDataPath);

			while (isDecompressing == false) {
				yield return null;
			}

			decompressedModelPath = targetDirectory;

			yield return new WaitForSeconds(1);
			zipFile.Dispose();
		}

		private void ZipFileOnExtractProgress(object sender, ExtractProgressEventArgs e) {
			if (e.EventType == ZipProgressEventType.Extracting_AfterExtractAll) {
				isDecompressing = true;
				decompressedModelPath = e.ExtractLocation;
			}
		}

		private IEnumerator WaitForMicrophoneInput() {
			while (Microphone.devices.Length <= 0) {
				yield return null;
			}
		}

		// Parses the Vosk JSON, triggers events, and pushes the actual text to the UI
		private void ProcessTranscription(string jsonResult, float latency) {
			RecognitionResult result = new RecognitionResult(jsonResult);

			if (result.Phrases == null || result.Phrases.Length == 0) {
				return;
			}

			bool latencyLoggedForThisBatch = false;

			foreach (RecognizedPhrase p in result.Phrases) {
				string spokenText = p.Text.Trim().ToLower(); // Normalize to lowercase for safe matching

				// Ignore empty results and the [unk] fallback token
				if (!string.IsNullOrEmpty(spokenText) && spokenText != "[unk]") {

					// Only log latency if we actually have a valid word to show, and only once per batch
					if (trackLatency && latency > 0 && !latencyLoggedForThisBatch) {
						LogLatency(latency);
						latencyLoggedForThisBatch = true;
					}

					Debug.Log($"Vosk Recognized: {spokenText}");

					HandleRecognizedText(spokenText);
				}
			}
		}

		// Shared logic for processing a clean, spoken phrase, regardless of where it came from
		private void HandleRecognizedText(string spokenText) {
			if (outputText != null) {
				outputText.text += spokenText + "\n";
			}

			// Trigger the global event in case external scripts want the raw text
			onAnyTextRecognized?.Invoke(spokenText);

			// Process specific voice commands
			foreach (VoiceCommand cmd in voiceCommands) {
				if (string.IsNullOrWhiteSpace(cmd.phrase)) {
					continue;
				}

				string targetPhrase = cmd.phrase.ToLower().Trim();

				bool isMatch = requireExactMatch
					? spokenText == targetPhrase
					: spokenText.Contains(targetPhrase);

				if (isMatch) {
					// Invoke the UnityEvent just like clicking a UI Button
					cmd.onRecognized?.Invoke();
				}
			}
		}

		// Logs the calculated latency to the console
		private void LogLatency(float latencyInSeconds) {
			Debug.Log($"[Latency] Word detection to final output: {latencyInSeconds:F3} seconds");
		}

		private void VoiceProcessorOnFrameCaptured(short[] samples) {
			if (running) {
				threadedBufferQueue.Enqueue(samples);
			}
		}

		private void VoiceProcessorOnRecordingStop() {
			Debug.Log("VoskVoiceListener: Microphone capture halted");
		}

		// Background worker that actually crunches the audio data
		private async Task ThreadedWork() {
			voskRecognizerCreateMarker.Begin();
			if (!recognizerReady) {
				UpdateGrammar();

				if (string.IsNullOrEmpty(grammar)) {
					recognizer = new VoskRecognizer(model, 16000.0f);
				}
				else {
					recognizer = new VoskRecognizer(model, 16000.0f, grammar);
				}

				recognizer.SetMaxAlternatives(maxAlternatives);
				recognizerReady = true;
			}
			voskRecognizerCreateMarker.End();

			voskRecognizerReadMarker.Begin();

			Stopwatch latencyTimer = new Stopwatch();

			while (running) {
				if (threadedBufferQueue.TryDequeue(out short[] voiceResult)) {
					if (recognizer.AcceptWaveform(voiceResult, voiceResult.Length)) {
						string result = recognizer.Result();
						float processingLatency = 0f;

						if (trackLatency) {
							latencyTimer.Stop();
							processingLatency = (float)latencyTimer.Elapsed.TotalSeconds;
							latencyTimer.Reset();
						}

						threadedResultQueue.Enqueue(new VoskResult { json = result, latency = processingLatency });
					}
					else if (trackLatency) {
						// Check partial results to see if Vosk has confidently identified a word
						if (!latencyTimer.IsRunning) {
							string partial = recognizer.PartialResult();

							// Vosk returns { "partial" : "" } when it hears noise but no words yet
							if (!partial.Contains("\"partial\" : \"\"")) {
								latencyTimer.Start();
							}
						}
					}
				}
				else {
					// Yield to avoid locking up the thread when the buffer is empty
					await Task.Delay(100);
				}
			}
			voskRecognizerReadMarker.End();
		}
	}

	/// <summary>
	/// Serializable class to map a specific spoken phrase to Unity Events in the Inspector.
	/// </summary>
	[Serializable]
	public class VoiceCommand {
		[Tooltip("The exact word or phrase to detect (will be converted to lowercase)")]
		public string phrase;

		[Tooltip("Functions to trigger when this phrase is recognized")]
		public UnityEvent onRecognized;
	}
}
