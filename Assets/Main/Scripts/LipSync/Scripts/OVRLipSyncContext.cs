/************************************************************************************
Filename    :   OVRLipSyncContext.cs
Content     :   Interface to Oculus Lip-Sync engine
Created     :   August 6th, 2015
Copyright   :   Copyright Facebook Technologies, LLC and its affiliates.
                All rights reserved.

Licensed under the Oculus Audio SDK License Version 3.3 (the "License");
you may not use the Oculus Audio SDK except in compliance with the License,
which is provided at the time of installation or download, or which
otherwise accompanies this software in either electronic or hard copy form.

You may obtain a copy of the License at

https://developer.oculus.com/licenses/audio-3.3/

Unless required by applicable law or agreed to in writing, the Oculus Audio SDK
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
************************************************************************************/
using UnityEngine;

[RequireComponent(typeof(AudioSource))]

//-------------------------------------------------------------------------------------
// ***** OVRLipSyncContext
//
/// <summary>
/// OVRLipSyncContext interfaces into the Oculus phoneme recognizer.
/// This component should be added into the scene once for each Audio Source.
///
/// </summary>
public class OVRLipSyncContext : OVRLipSyncContextBase
{
    // * * * * * * * * * * * * *
    // Public members


    [Tooltip("Allow capturing of keyboard input to control operation.")]
    public bool enableKeyboardInput = false;
    [Tooltip("Play input audio back through audio output.")]
    public bool audioLoopback = false;
    [Tooltip("Key to toggle audio loopback.")]
    public KeyCode loopbackKey = KeyCode.L;
    [Tooltip("Skip data from the Audio Source. Use if you intend to pass audio data in manually.")]
    public bool skipAudioSource = false;
    [Tooltip("Adjust the linear audio gain multiplier before processing lipsync")]
    public float gain = 1.0f;

    public float laughterScore = 0.0f;

    // * * * * * * * * * * * * *
    // Private members

    /// <summary>
    /// Start this instance.
    /// Note: make sure to always have a Start function for classes that have editor scripts.
    /// </summary>
    void Start()
    {
    }

    /// <summary>
    /// Handle keyboard input
    /// </summary>
    void HandleKeyboard()
    {
#if ENABLE_LEGACY_INPUT_MANAGER
        if (Input.GetKeyDown(loopbackKey))
        {
            ToggleAudioLoopback();
        }
        else if (Input.GetKeyDown(KeyCode.LeftArrow))
        {
            gain = Mathf.Max(gain - 1.0f, 1.0f);
        }
        else if (Input.GetKeyDown(KeyCode.RightArrow))
        {
            gain = Mathf.Min(gain + 1.0f, 15.0f);
        }
#endif
    }

    /// <summary>
    /// Run processes that need to be updated in our game thread
    /// </summary>
    void Update()
    {
        if (enableKeyboardInput)
        {
            HandleKeyboard();
        }
        laughterScore = this.Frame.laughterScore;
    }

    /// <summary>
    /// Preprocess F32 PCM audio buffer
    /// </summary>
    /// <param name="data">Data.</param>
    /// <param name="channels">Channels.</param>
    public void PreprocessAudioSamples(float[] data, int channels)
    {
        // Increase the gain of the input
        for (int i = 0; i < data.Length; ++i)
        {
            data[i] = data[i] * gain;
        }
    }

    /// <summary>
    /// Postprocess F32 PCM audio buffer
    /// </summary>
    /// <param name="data">Data.</param>
    /// <param name="channels">Channels.</param>
    public void PostprocessAudioSamples(float[] data, int channels)
    {
        // Turn off output (so that we don't get feedback from mics too close to speakers)
        if (!audioLoopback)
        {
            for (int i = 0; i < data.Length; ++i)
                data[i] = data[i] * 0.0f;
        }
    }

    /// <summary>
    /// Pass F32 PCM audio buffer to the lip sync module
    /// </summary>
    /// <param name="data">Data.</param>
    /// <param name="channels">Channels.</param>
    public void ProcessAudioSamplesRaw(float[] data, int channels)
    {
        // Send data into Phoneme context for processing (if context is not 0)
        lock (this)
        {
            if (Context == 0 || OVRLipSync.IsInitialized() != OVRLipSync.Result.Success)
            {
                return;
            }
            var frame = this.Frame;
            OVRLipSync.ProcessFrame(Context, data, frame, channels == 2);
        }
    }

    /// <summary>
    /// Pass S16 PCM audio buffer to the lip sync module
    /// </summary>
    /// <param name="data">Data.</param>
    /// <param name="channels">Channels.</param>
    public void ProcessAudioSamplesRaw(short[] data, int channels)
    {
        // Send data into Phoneme context for processing (if context is not 0)
        lock (this)
        {
            if (Context == 0 || OVRLipSync.IsInitialized() != OVRLipSync.Result.Success)
            {
                return;
            }
            var frame = this.Frame;
            OVRLipSync.ProcessFrame(Context, data, frame, channels == 2);
        }
    }


    /// <summary>
    /// Process F32 audio sample and pass it to the lip sync module for computation
    /// </summary>
    /// <param name="data">Data.</param>
    /// <param name="channels">Channels.</param>
    public void ProcessAudioSamples(float[] data, int channels)
    {
        // Do not process if we are not initialized, or if there is no
        // audio source attached to game object
        if ((OVRLipSync.IsInitialized() != OVRLipSync.Result.Success) || audioSource == null)
        {
            return;
        }
        PreprocessAudioSamples(data, channels);
        ProcessAudioSamplesRaw(data, channels);
        PostprocessAudioSamples(data, channels);
    }

    /// <summary>
    /// Raises the audio filter read event.
    /// </summary>
    /// <param name="data">Data.</param>
    /// <param name="channels">Channels.</param>
    void OnAudioFilterRead(float[] data, int channels)
    {
        if (!skipAudioSource)
        {
            ProcessAudioSamples(data, channels);
        }
    }

    /// <summary>
    /// Print the visemes and laughter score to game window
    /// </summary>
    void ToggleAudioLoopback()
    {
        audioLoopback = !audioLoopback;
    }
}
