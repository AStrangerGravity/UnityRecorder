using System;
using System.Collections.Generic;
using Core.Harness;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEditor.Recorder;
using UnityEditor.Recorder.Input;
using UnityEditor.Media;
using Unity.Media;
using static UnityEditor.Recorder.MovieRecorderSettings;

namespace UnityEditor.Recorder
{
    class MovieRecorder : BaseTextureRecorder<MovieRecorderSettings>
    {
        MediaEncoderHandle m_EncoderHandle = new MediaEncoderHandle();

        /// <summary>
        /// The count of concurrent Movie Recorder instances. It is used to log a warning.
        /// </summary>
        static private int s_ConcurrentCount = 0;

        /// <summary>
        /// Whether or not a warning was logged for concurrent movie recorders.
        /// </summary>
        static private bool s_WarnedUserOfConcurrentCount = false;

        /// <summary>
        /// Whether or not recording was started properly.
        /// </summary>
        private bool m_RecordingStartedProperly = false;

        /// <summary>
        /// The frame rate the encoder is set to. This is fixed for the entire recording. Video encoders use rational
        /// numbers for frame rates to avoid rounding error.
        /// </summary>
        private MediaRational m_FrameRate;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        private static void EnterPlayMode()
        {
            s_ConcurrentCount = 0;
            s_WarnedUserOfConcurrentCount = false;
        }

        protected override TextureFormat ReadbackTextureFormat
        {
            get
            {
                return Settings.GetCurrentEncoder().GetTextureFormat(Settings);
            }
        }

        protected internal override bool BeginRecording(RecordingSession session)
        {
            m_RecordingStartedProperly = false;
            if (!base.BeginRecording(session))
                return false;

            try
            {
                Settings.fileNameGenerator.CreateDirectory(session);
            }
            catch (Exception)
            {
                Debug.LogError(string.Format("Movie recorder output directory \"{0}\" could not be created.", Settings.fileNameGenerator.BuildAbsolutePath(session)));
                return false;
            }

            var input = m_Inputs[0] as BaseRenderTextureInput;
            if (input == null)
            {
                Debug.LogError("MediaRecorder could not find input.");
                return false;
            }
            int width = input.OutputWidth;
            int height = input.OutputHeight;

            if (width <= 0 || height <= 0)
            {
                Debug.LogError(string.Format("MovieRecorder got invalid input resolution {0} x {1}.", width, height));
                return false;
            }

            var currentEncoderReg = Settings.GetCurrentEncoder();
            string erroMessage;
            if (!currentEncoderReg.SupportsResolution(Settings, width, height, out erroMessage))
            {
                Debug.LogError(erroMessage);
                return false;
            }

            var imageInputSettings = m_Inputs[0].settings as ImageInputSettings;

            var alphaWillBeInImage = imageInputSettings != null && imageInputSettings.SupportsTransparent && imageInputSettings.RecordTransparency;
            if (alphaWillBeInImage && !currentEncoderReg.SupportsTransparency(Settings, out erroMessage))
            {
                Debug.LogError(erroMessage);
                return false;
            }

            // In variable frame rate mode, we set the encoder to the frame rate of the current display.
            m_FrameRate = RationalFromDouble(
                session.settings.FrameRatePlayback == FrameRatePlayback.Variable
                    ? GameHarness.DisplayFPSTarget
                    : session.settings.FrameRate);

            var videoAttrs = new VideoTrackAttributes
            {
                width = (uint)width,
                height = (uint)height,
                frameRate = m_FrameRate,
                includeAlpha = alphaWillBeInImage,
                bitRateMode = Settings.VideoBitRateMode
            };

            Debug.Log($"(UnityRecorder/MovieRecorder) Encoding video " +
                $"{width}x{height}@[{videoAttrs.frameRate.numerator}/{videoAttrs.frameRate.denominator}] fps into " +
                $"{Settings.fileNameGenerator.BuildAbsolutePath(session)}");

            var audioInput = (AudioInputBase) m_Inputs[1];
            var audioAttrsList = new List<AudioTrackAttributes>();

            if (audioInput.audioSettings.PreserveAudio)
            {
#if UNITY_EDITOR_OSX
                // Special case with WebM and audio on older Apple computers: deactivate async GPU readback because there
                // is a risk of not respecting the WebM standard and receiving audio frames out of sync (see "monotonically
                // increasing timestamps"). This happens only with Target Cameras.
                if (m_Inputs[0].settings is CameraInputSettings && Settings.OutputFormat == VideoRecorderOutputFormat.WebM)
                {
                    UseAsyncGPUReadback = false;
                }
#endif
                var audioAttrs = new AudioTrackAttributes
                {
                    sampleRate = new MediaRational
                    {
                        numerator = audioInput.sampleRate,
                        denominator = 1
                    },
                    channelCount = audioInput.channelCount,
                    language = ""
                };

                audioAttrsList.Add(audioAttrs);

                if (RecorderOptions.VerboseMode)
                    Debug.Log(string.Format("MovieRecorder starting to write audio {0}ch @ {1}Hz", audioAttrs.channelCount, audioAttrs.sampleRate.numerator));
            }
            else
            {
                if (RecorderOptions.VerboseMode)
                    Debug.Log("MovieRecorder starting with no audio.");
            }

            try
            {
                var path =  Settings.fileNameGenerator.BuildAbsolutePath(session);

                // If an encoder already exist destroy it
                Settings.DestroyIfExists(m_EncoderHandle);

                // Get the currently selected encoder register and create an encoder
                m_EncoderHandle = currentEncoderReg.Register(Settings.m_EncoderManager);

                // Create the list of attributes for the encoder, Video, Audio and preset
                // TODO: Query the list of attributes from the encoder attributes
                var attr = new List<IMediaEncoderAttribute>();
                attr.Add(new VideoTrackMediaEncoderAttribute("VideoAttributes", videoAttrs));

                if (audioInput.audioSettings.PreserveAudio)
                {
                    if (audioAttrsList.Count > 0)
                    {
                        attr.Add(new AudioTrackMediaEncoderAttribute("AudioAttributes", audioAttrsList.ToArray()[0]));
                    }
                }

                attr.Add(new IntAttribute(AttributeLabels[MovieRecorderSettingsAttributes.CodecFormat], Settings.encoderPresetSelected));
                attr.Add(new IntAttribute(AttributeLabels[MovieRecorderSettingsAttributes.ColorDefinition], Settings.encoderColorDefinitionSelected));

                if (Settings.encoderPresetSelectedName == "Custom")
                {
                    // For custom
                    attr.Add(new StringAttribute(AttributeLabels[MovieRecorderSettingsAttributes.CustomOptions], Settings.encoderCustomOptions));
                }
                // Construct the encoder given the list of attributes
                Settings.m_EncoderManager.Construct(m_EncoderHandle, path, attr);

                s_ConcurrentCount++;

                m_RecordingStartedProperly = true;
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError("MovieRecorder unable to create MovieEncoder. " + ex.Message);
                return false;
            }
        }

        protected internal override void RecordFrame(RecordingSession session)
        {
            if (m_Inputs.Count != 2)
                throw new Exception("Unsupported number of sources");

            base.RecordFrame(session);
            var audioInput = (AudioInputBase) m_Inputs[1];
            if (audioInput.audioSettings.PreserveAudio && lastFrame > -1)
                Settings.m_EncoderManager.AddSamples(m_EncoderHandle, audioInput.mainBuffer);
        }

        protected internal override void EndRecording(RecordingSession session)
        {
            base.EndRecording(session);
            if (m_RecordingStartedProperly)
            {
                s_ConcurrentCount--;
                if (s_ConcurrentCount < 0)
                    Debug.LogError($"Recording ended with no matching beginning recording.");
                if (s_ConcurrentCount <= 1 && s_WarnedUserOfConcurrentCount)
                    s_WarnedUserOfConcurrentCount = false; // reset so that we can warn at the next occurence
            }
        }

        /// <summary>
        /// Detect the ocurrence of concurrent recorders.
        /// </summary>
        private void WarnOfConcurrentRecorders()
        {
            if (s_ConcurrentCount > 1 && !s_WarnedUserOfConcurrentCount)
            {
                Debug.LogWarning($"There are two or more concurrent Movie Recorders. You may experience slowdowns and other issues, since this is not recommended.");
                s_WarnedUserOfConcurrentCount = true;
            }
        }

        protected override void WriteFrame(Texture2D t)
        {
            Settings.m_EncoderManager.AddFrame(m_EncoderHandle, t);
            WarnOfConcurrentRecorders();
        }

        private long lastFrame = -1;

#if UNITY_2019_1_OR_NEWER
        protected override void WriteFrame(AsyncGPUReadbackRequest r, double timestamp)
        {
            var format = Settings.GetCurrentEncoder().GetTextureFormat(Settings);

            if (Settings.FrameRatePlayback == FrameRatePlayback.Variable)
            {
                // The closest media frame to the actual frame's timestamp.
                int frame = Mathf.RoundToInt((float) (timestamp / (1.0 / (double) m_FrameRate)));
                MediaTime time = new MediaTime(frame, (uint) m_FrameRate.numerator, (uint) m_FrameRate.denominator);

                if (time.count > lastFrame) // If two render frames fall on the same encoding frame, ignore.
                {
                    Settings.m_EncoderManager.AddFrame(m_EncoderHandle, r.width, r.height, 0, format, r.GetData<byte>(),
                        time);
                }

                lastFrame = time.count;
            }
            else
            {
                Settings.m_EncoderManager.AddFrame(m_EncoderHandle, r.width, r.height, 0, format, r.GetData<byte>());
            }

            WarnOfConcurrentRecorders();
        }

#endif

        protected override void DisposeEncoder()
        {
            if (!Settings.m_EncoderManager.Exists(m_EncoderHandle))
            {
                base.DisposeEncoder();
                return;
            }

            Settings.m_EncoderManager.Destroy(m_EncoderHandle);
            base.DisposeEncoder();

            // When adding a file to Unity's assets directory, trigger a refresh so it is detected.
            if (Settings.fileNameGenerator.Root == OutputPath.Root.AssetsFolder || Settings.fileNameGenerator.Root == OutputPath.Root.StreamingAssets)
                AssetDatabase.Refresh();
        }

        // https://stackoverflow.com/questions/26643695/converting-decimal-to-fraction-c
        static long GreatestCommonDivisor(long a, long b)
        {
            if (a == 0)
                return b;

            if (b == 0)
                return a;

            return (a < b) ? GreatestCommonDivisor(a, b % a) : GreatestCommonDivisor(b, a % b);
        }

        static MediaRational RationalFromDouble(double value)
        {
            var integral = Math.Floor(value);
            var frac = value - integral;

            const long precision = 10000000;

            var gcd = GreatestCommonDivisor((long)Math.Round(frac * precision), precision);
            var denom = precision / gcd;

            return new MediaRational()
            {
                numerator = (int)((long)integral * denom + ((long)Math.Round(frac * precision)) / gcd),
                denominator = (int)denom
            };
        }
    }
}
