using System;
using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Collections.Generic;

namespace RGHAudioConverter
{
    public enum OutputFormat
    {
        SNS,
        SON
    }

    public enum ExtrasOption
    {
        None,
        JustDance,
        CustomBeats
    }

    public static class LynEncoder
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AllocConsole();

        // DSP coefs from VGMStream
        private static readonly short[] DSP_COEFS =
        {
            0x04ab, unchecked((short)0xfced),
            0x0789, unchecked((short)0xfedf),
            0x09a2, unchecked((short)0xfae5),
            0x0c90, unchecked((short)0xfac1),
            0x084d, unchecked((short)0xfaa4),
            0x0982, unchecked((short)0xfdf7),
            0x0af6, unchecked((short)0xfafa),
            0x0be6, unchecked((short)0xfbf5)
        };

        // beat stealer
        private static byte[]? _customBeatData = null;
        private static int _customBeatCount = 0;
        public static int CustomBeatCount => _customBeatCount;
        public static bool HasCustomBeats => _customBeatData != null && _customBeatData.Length > 0;

        public static int ExtractBeatsFromSns(string snsPath)
        {
            try
            {
                byte[] snsData = File.ReadAllBytes(snsPath);
                
                // find cue chunk
                int cuePos = -1;
                for (int i = 0; i < snsData.Length - 4; i++)
                {
                    if (snsData[i] == 'c' && snsData[i + 1] == 'u' && snsData[i + 2] == 'e' && snsData[i + 3] == ' ')
                    {
                        cuePos = i;
                        break;
                    }
                }

                if (cuePos == -1)
                {
                    _customBeatData = null;
                    _customBeatCount = 0;
                    return -1;
                }

                int cueSize = BitConverter.ToInt32(snsData, cuePos + 4);
                int beatCount = BitConverter.ToInt32(snsData, cuePos + 8);

                // find data chunk
                int dataPos = -1;
                for (int i = cuePos + 8 + cueSize; i < snsData.Length - 4; i++)
                {
                    if (snsData[i] == 'd' && snsData[i + 1] == 'a' && snsData[i + 2] == 't' && snsData[i + 3] == 'a')
                    {
                        dataPos = i;
                        break;
                    }
                }

                if (dataPos == -1)
                {
                    _customBeatData = null;
                    _customBeatCount = 0;
                    return -1;
                }

                int beatDataLength = dataPos - cuePos;
                _customBeatData = new byte[beatDataLength];
                Array.Copy(snsData, cuePos, _customBeatData, 0, beatDataLength);
                _customBeatCount = beatCount;

                return beatCount;
            }
            catch
            {
                _customBeatData = null;
                _customBeatCount = 0;
                return -1;
            }
        }

        public static void ClearCustomBeats()
        {
            _customBeatData = null;
            _customBeatCount = 0;
        }

        private static byte[] GetCustomBeatData()
        {
            return _customBeatData ?? Array.Empty<byte>();
        }

        public static bool ValidateWavFile(string path, out string error)
        {
            error = "";
            try
            {
                byte[] fileBytes = File.ReadAllBytes(path);

                if (fileBytes.Length < 44)
                {
                    error = "WAV file is too small";
                    return false;
                }

                if (fileBytes[0] != 'R' || fileBytes[1] != 'I' || fileBytes[2] != 'F' || fileBytes[3] != 'F')
                {
                    error = "Not a valid WAV file";
                    return false;
                }

                if (fileBytes[8] != 'W' || fileBytes[9] != 'A' || fileBytes[10] != 'V' || fileBytes[11] != 'E')
                {
                    error = "Not a valid WAV file";
                    return false;
                }

                int pos = 12;
                while (pos < fileBytes.Length - 8)
                {
                    string chunkId = System.Text.Encoding.ASCII.GetString(fileBytes, pos, 4);
                    int chunkSize = BitConverter.ToInt32(fileBytes, pos + 4);

                    if (chunkSize < 0 || pos + 8 + chunkSize > fileBytes.Length)
                        break;

                    if (chunkId == "fmt ")
                    {
                        var audioFormat = BitConverter.ToInt16(fileBytes, pos + 8);
                        var bitsPerSample = BitConverter.ToInt16(fileBytes, pos + 22);

                        if (audioFormat != 1)
                        {
                            error = "Only uncompressed PCM WAV files are supported";
                            return false;
                        }

                        if (bitsPerSample != 16)
                        {
                            error = $"Only 16-bit WAV files are supported (got {bitsPerSample}-bit)";
                            return false;
                        }

                        return true;
                    }

                    pos += 8 + chunkSize;
                    if (pos % 2 != 0) pos++;
                }

                error = "Invalid WAV file";
                return false;
            }
            catch (Exception ex)
            {
                error = $"Could not read WAV file: {ex.Message}";
                return false;
            }
        }

        public static string? ConvertWavToDspSns(string inputPath, string outputPath, Action<int>? progressCallback = null, bool debugMode = false, int targetSampleRate = 32000, bool forceMono = false, OutputFormat format = OutputFormat.SNS, bool normalize = true, bool fourChannel = false, ExtrasOption extras = ExtrasOption.None)
        {
            string? tempProcessedFile = null;

            try
            {
                if (debugMode)
                {
                    AllocConsole();
                    Console.WriteLine("=== RGH Audio Converter - DSP ===");
                    Console.WriteLine($"Input: {inputPath}");
                    Console.WriteLine($"Output: {outputPath}");
                    Console.WriteLine($"Sample Rate: {targetSampleRate}Hz");
                    Console.WriteLine($"Mono: {forceMono}");
                    Console.WriteLine($"Format: {format}");
                    Console.WriteLine($"Normalize: {normalize}");
                    Console.WriteLine($"Extras: {extras}");
                    Console.WriteLine();
                }

                progressCallback?.Invoke(0);

                string? ffmpegPath = FindExecutable("ffmpeg");

                if (debugMode)
                {
                    Console.WriteLine($"ffmpeg: {ffmpegPath ?? "NOT FOUND"}");
                    Console.WriteLine();
                }

                // get input info
                byte[] checkBytes = File.ReadAllBytes(inputPath);
                int inputSampleRate = 0;
                int inputChannels = 0;
                int checkPos = 12;
                while (checkPos < checkBytes.Length - 8)
                {
                    string chunkId = System.Text.Encoding.ASCII.GetString(checkBytes, checkPos, 4);
                    int chunkSize = BitConverter.ToInt32(checkBytes, checkPos + 4);
                    if (chunkSize < 0 || checkPos + 8 + chunkSize > checkBytes.Length) break;
                    if (chunkId == "fmt ")
                    {
                        inputChannels = BitConverter.ToInt16(checkBytes, checkPos + 10);
                        inputSampleRate = BitConverter.ToInt32(checkBytes, checkPos + 12);
                        break;
                    }
                    checkPos += 8 + chunkSize;
                    if (checkPos % 2 != 0) checkPos++;
                }

                string actualInputPath = inputPath;
                bool needsProcessing = (inputSampleRate != targetSampleRate && inputSampleRate > 0) ||
                                       (forceMono && inputChannels > 1) ||
                                       normalize;

                if (needsProcessing && ffmpegPath != null)
                {
                    if (debugMode) Console.WriteLine($"[5%] Processing audio...");
                    progressCallback?.Invoke(5);

                    tempProcessedFile = Path.Combine(Path.GetTempPath(), $"rgh_process_{Guid.NewGuid():N}.wav");

                    string channelArg = (forceMono && inputChannels > 1) ? "-ac 1" : "";
                    string rateArg = (inputSampleRate != targetSampleRate) ? $"-ar {targetSampleRate}" : "";
                    string normalizeArg = normalize ? "-af loudnorm=I=-16:TP=-1.5:LRA=11" : "";

                    var result = RunProcess(ffmpegPath,
                        $"-y -i \"{inputPath}\" {channelArg} {rateArg} {normalizeArg} \"{tempProcessedFile}\"",
                        debugMode);

                    if (result == 0 && File.Exists(tempProcessedFile))
                    {
                        actualInputPath = tempProcessedFile;
                        if (debugMode) Console.WriteLine($"  Done");
                    }
                    else
                    {
                        if (debugMode) Console.WriteLine($"  Failed, using original");
                    }
                }
                else if (needsProcessing && ffmpegPath == null)
                {
                    if (debugMode) Console.WriteLine($"  ffmpeg not found, skipping preprocessing");
                }

                if (debugMode) Console.WriteLine("[10%] Reading WAV...");

                byte[] fileBytes = File.ReadAllBytes(actualInputPath);

                if (fileBytes.Length < 44)
                    return "WAV file is too small";

                if (fileBytes[0] != 'R' || fileBytes[1] != 'I' || fileBytes[2] != 'F' || fileBytes[3] != 'F')
                    return "Not a valid WAV file";

                if (fileBytes[8] != 'W' || fileBytes[9] != 'A' || fileBytes[10] != 'V' || fileBytes[11] != 'E')
                    return "Not a valid WAV file";

                int channels = 0;
                int sampleRate = 0;
                byte[]? audioData = null;
                int pos = 12;

                while (pos < fileBytes.Length - 8)
                {
                    string chunkId = System.Text.Encoding.ASCII.GetString(fileBytes, pos, 4);
                    int chunkSize = BitConverter.ToInt32(fileBytes, pos + 4);

                    if (chunkSize < 0 || pos + 8 + chunkSize > fileBytes.Length)
                    {
                        if (debugMode) Console.WriteLine($"  Bad chunk '{chunkId}' size {chunkSize}");
                        break;
                    }

                    if (chunkId == "fmt ")
                    {
                        channels = BitConverter.ToInt16(fileBytes, pos + 10);
                        sampleRate = BitConverter.ToInt32(fileBytes, pos + 12);
                        if (debugMode) Console.WriteLine($"  fmt: {channels}ch, {sampleRate}Hz");
                    }
                    else if (chunkId == "data")
                    {
                        audioData = new byte[chunkSize];
                        Array.Copy(fileBytes, pos + 8, audioData, 0, chunkSize);
                        if (debugMode) Console.WriteLine($"  data: {chunkSize} bytes");
                    }

                    pos += 8 + chunkSize;
                    if (pos % 2 != 0) pos++;
                }

                if (audioData == null)
                    return "No audio data found";

                if (channels == 0 || sampleRate == 0)
                    return "Invalid WAV format";

                if (debugMode)
                {
                    Console.WriteLine($"  Channels: {channels}");
                    Console.WriteLine($"  Sample Rate: {sampleRate} Hz");
                    Console.WriteLine($"  Audio Data: {audioData.Length} bytes");
                }

                progressCallback?.Invoke(10);

                if (debugMode) Console.WriteLine("[20%] Converting samples...");

                int numSamples = audioData.Length / 2;
                int numFrames = numSamples / channels;
                short[] samples = new short[numSamples];

                for (int i = 0; i < numSamples; i++)
                {
                    samples[i] = BitConverter.ToInt16(audioData, i * 2);
                }

                progressCallback?.Invoke(20);

                if (debugMode) Console.WriteLine($"[20%] Encoding {numFrames} frames...");

                byte[] dspData;
                int outputChannels = channels;

                // 4ch interleaving
                if (fourChannel && format == OutputFormat.SON && channels == 2)
                {
                    short[] leftSamples = new short[numFrames];
                    short[] rightSamples = new short[numFrames];

                    for (int i = 0; i < numFrames; i++)
                    {
                        leftSamples[i] = samples[i * 2];
                        rightSamples[i] = samples[i * 2 + 1];
                    }

                    if (debugMode) Console.WriteLine("  Encoding 4 channels (L, R, L copy, R copy)...");

                    byte[] leftEncoded = EncodeDspData(leftSamples, progressCallback, 20, 40);
                    byte[] rightEncoded = EncodeDspData(rightSamples, progressCallback, 40, 60);
                    byte[] leftEncoded2 = EncodeDspData(leftSamples, progressCallback, 60, 80);
                    byte[] rightEncoded2 = EncodeDspData(rightSamples, progressCallback, 80, 90);

                    dspData = InterleaveDspChannels4(leftEncoded, rightEncoded, leftEncoded2, rightEncoded2);
                    outputChannels = 4;
                }
                else if (channels == 1)
                {
                    if (debugMode) Console.WriteLine("  Mono");
                    dspData = EncodeDspData(samples, progressCallback, 20, 90);
                }
                else
                {
                    short[] leftSamples = new short[numFrames];
                    short[] rightSamples = new short[numFrames];

                    for (int i = 0; i < numFrames; i++)
                    {
                        leftSamples[i] = samples[i * 2];
                        rightSamples[i] = samples[i * 2 + 1];
                    }

                    if (debugMode) Console.WriteLine("  Left channel...");
                    byte[] leftEncoded = EncodeDspData(leftSamples, progressCallback, 20, 55);

                    if (debugMode) Console.WriteLine("  Right channel...");
                    byte[] rightEncoded = EncodeDspData(rightSamples, progressCallback, 55, 90);

                    if (debugMode) Console.WriteLine("  Interleaving...");
                    dspData = InterleaveDspChannels(leftEncoded, rightEncoded);
                }

                progressCallback?.Invoke(95);

                if (debugMode) Console.WriteLine($"[95%] Building {format}...");

                byte[] outputData;

                if (format == OutputFormat.SON)
                {
                    if (fourChannel && outputChannels == 4)
                    {
                        outputData = CreateDspSon4Ch(dspData, targetSampleRate, numFrames);
                    }
                    else
                    {
                        outputData = CreateDspSon(dspData, outputChannels, targetSampleRate, numFrames);
                    }
                }
                else
                {
                    outputData = CreateDspSns(dspData, outputChannels, targetSampleRate, numFrames, extras);

                    if (extras == ExtrasOption.JustDance)
                    {
                        byte[] jdHeader = new byte[] {
                            0x4C, 0x79, 0x53, 0x45,
                            0x0C, 0x00, 0x00, 0x00,
                            0x00, 0x00, 0x00, 0x00,
                            0x0C, 0x00, 0x00, 0x00,
                            0x1F, 0x00, 0x00, 0x00
                        };
                        byte[] combined = new byte[jdHeader.Length + outputData.Length];
                        Array.Copy(jdHeader, 0, combined, 0, jdHeader.Length);
                        Array.Copy(outputData, 0, combined, jdHeader.Length, outputData.Length);
                        outputData = combined;

                        if (debugMode) Console.WriteLine("  Added Just Dance LySE header");
                    }
                }

                File.WriteAllBytes(outputPath, outputData);

                progressCallback?.Invoke(100);

                if (debugMode)
                {
                    Console.WriteLine("[100%] Done!");
                    Console.WriteLine($"  Output: {outputData.Length} bytes");
                    Console.WriteLine();
                    Console.WriteLine("Press any key to close...");
                    Console.ReadKey();
                }

                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
            finally
            {
                if (tempProcessedFile != null)
                {
                    try { File.Delete(tempProcessedFile); } catch { }
                }
            }
        }

        private static byte[] EncodeDspData(short[] samples, Action<int>? progressCallback = null, int progressStart = 0, int progressEnd = 100)
        {
            int numSamples = samples.Length;
            int numFrames = (numSamples + 13) / 14;
            byte[] result = new byte[numFrames * 8];

            int hist1 = 0;
            int hist2 = 0;
            int sampleIdx = 0;
            int lastProgress = -1;

            for (int frameIdx = 0; frameIdx < numFrames; frameIdx++)
            {
                if (progressCallback != null)
                {
                    int progress = progressStart + (frameIdx * (progressEnd - progressStart) / numFrames);
                    if (progress != lastProgress)
                    {
                        progressCallback(progress);
                        lastProgress = progress;
                    }
                }

                int frameOffset = frameIdx * 8;

                short[] frameSamples = new short[14];
                for (int i = 0; i < 14; i++)
                {
                    if (sampleIdx + i < numSamples)
                        frameSamples[i] = samples[sampleIdx + i];
                    else
                        frameSamples[i] = 0;
                }

                // try each coef, pick lowest error
                int bestCoef = 0;
                int bestScale = 0;
                long bestTotalError = long.MaxValue;
                int[] bestNibbles = new int[14];
                int bestHist1 = hist1;
                int bestHist2 = hist2;

                for (int coefIdx = 0; coefIdx < 8; coefIdx++)
                {
                    int c1 = DSP_COEFS[coefIdx * 2];
                    int c2 = DSP_COEFS[coefIdx * 2 + 1];

                    int th1 = hist1, th2 = hist2;
                    int maxResidual = 0;

                    for (int i = 0; i < 14; i++)
                    {
                        int pred = (c1 * th1 + c2 * th2 + 1024) >> 11;
                        int res = frameSamples[i] - pred;
                        if (Math.Abs(res) > maxResidual)
                            maxResidual = Math.Abs(res);
                        th2 = th1;
                        th1 = frameSamples[i];
                    }

                    int scale = 0;
                    while (scale < 12 && maxResidual > ((1 << scale) * 8 - 1))
                        scale++;

                    int scaleFactor = 1 << scale;

                    int[] testNibbles = new int[14];
                    int testH1 = hist1, testH2 = hist2;
                    long totalError = 0;

                    for (int i = 0; i < 14; i++)
                    {
                        int pred = (c1 * testH1 + c2 * testH2 + 1024) >> 11;
                        int residual = frameSamples[i] - pred;

                        int nibble = (residual + (scaleFactor >> 1)) / scaleFactor;
                        nibble = Math.Clamp(nibble, -8, 7);
                        testNibbles[i] = nibble & 0xF;

                        int signedNibble = nibble >= 8 ? nibble - 16 : nibble;
                        int decoded = signedNibble * scaleFactor;
                        decoded = ((decoded << 11) + 1024 + c1 * testH1 + c2 * testH2) >> 11;
                        decoded = Math.Clamp(decoded, -32768, 32767);

                        int err = frameSamples[i] - decoded;
                        totalError += (long)err * err;

                        testH2 = testH1;
                        testH1 = decoded;
                    }

                    if (totalError < bestTotalError)
                    {
                        bestTotalError = totalError;
                        bestCoef = coefIdx;
                        bestScale = scale;
                        Array.Copy(testNibbles, bestNibbles, 14);
                        bestHist1 = testH1;
                        bestHist2 = testH2;
                    }
                }

                result[frameOffset] = (byte)((bestCoef << 4) | bestScale);

                for (int i = 0; i < 7; i++)
                {
                    result[frameOffset + 1 + i] = (byte)((bestNibbles[i * 2] << 4) | bestNibbles[i * 2 + 1]);
                }

                hist1 = bestHist1;
                hist2 = bestHist2;

                sampleIdx += 14;
            }

            return result;
        }

        private static byte[] InterleaveDspChannels(byte[] left, byte[] right)
        {
            int maxLen = Math.Max(left.Length, right.Length);

            if (left.Length < maxLen)
            {
                byte[] newLeft = new byte[maxLen];
                Array.Copy(left, newLeft, left.Length);
                left = newLeft;
            }
            if (right.Length < maxLen)
            {
                byte[] newRight = new byte[maxLen];
                Array.Copy(right, newRight, right.Length);
                right = newRight;
            }

            byte[] result = new byte[maxLen * 2];
            int numFrames = maxLen / 8;

            for (int i = 0; i < numFrames; i++)
            {
                int srcOff = i * 8;
                int dstOff = i * 16;
                Array.Copy(left, srcOff, result, dstOff, 8);
                Array.Copy(right, srcOff, result, dstOff + 8, 8);
            }

            return result;
        }

        private static byte[] InterleaveDspChannels4(byte[] ch1, byte[] ch2, byte[] ch3, byte[] ch4)
        {
            int maxLen = Math.Max(Math.Max(ch1.Length, ch2.Length), Math.Max(ch3.Length, ch4.Length));

            byte[][] channels = { ch1, ch2, ch3, ch4 };
            for (int c = 0; c < 4; c++)
            {
                if (channels[c].Length < maxLen)
                {
                    byte[] newCh = new byte[maxLen];
                    Array.Copy(channels[c], newCh, channels[c].Length);
                    channels[c] = newCh;
                }
            }

            byte[] result = new byte[maxLen * 4];
            int numFrames = maxLen / 8;

            for (int i = 0; i < numFrames; i++)
            {
                int srcOff = i * 8;
                int dstOff = i * 32;
                Array.Copy(channels[0], srcOff, result, dstOff, 8);
                Array.Copy(channels[1], srcOff, result, dstOff + 8, 8);
                Array.Copy(channels[2], srcOff, result, dstOff + 16, 8);
                Array.Copy(channels[3], srcOff, result, dstOff + 24, 8);
            }

            return result;
        }

        private static byte[] CreateDspSns(byte[] dspData, int channels, int sampleRate, int numSamples, ExtrasOption extras = ExtrasOption.None)
        {
            int dataSize = dspData.Length;
            int fmtSize = 0x12;
            int factSize = 0x10;

            byte[] beatChunk = Array.Empty<byte>();
            if (extras == ExtrasOption.CustomBeats)
            {
                beatChunk = GetCustomBeatData();
            }

            int blockAlign = 4;
            int bitsPerSample = 4;
            int avgBytesPerSec = 128000;

            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            int riffSize = 4 + (8 + fmtSize) + (8 + factSize) + beatChunk.Length + (8 + dataSize);

            writer.Write(new char[] { 'R', 'I', 'F', 'F' });
            writer.Write(riffSize);
            writer.Write(new char[] { 'W', 'A', 'V', 'E' });

            writer.Write(new char[] { 'f', 'm', 't', ' ' });
            writer.Write(fmtSize);
            writer.Write((short)0x5050);
            writer.Write((short)channels);
            writer.Write(sampleRate);
            writer.Write(avgBytesPerSec);
            writer.Write((short)blockAlign);
            writer.Write((short)bitsPerSample);
            writer.Write((short)0);

            writer.Write(new char[] { 'f', 'a', 'c', 't' });
            writer.Write(factSize);
            writer.Write(numSamples);
            writer.Write(new char[] { 'L', 'y', 'N', ' ' });
            writer.Write(0x03);
            writer.Write(0x07);

            if (beatChunk.Length > 0)
                writer.Write(beatChunk);

            writer.Write(new char[] { 'd', 'a', 't', 'a' });
            writer.Write(dataSize);
            writer.Write(dspData);

            return ms.ToArray();
        }

        private static byte[] CreateDspSon(byte[] dspData, int channels, int sampleRate, int numSamples)
        {
            int dataSize = dspData.Length;
            bool isLongAudio = numSamples > (sampleRate * 10);

            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            using var riffMs = new MemoryStream();
            using var riffWriter = new BinaryWriter(riffMs);

            // LySE chunk
            riffWriter.Write(new char[] { 'L', 'y', 'S', 'E' });
            riffWriter.Write(0x10);
            riffWriter.Write(0x01);
            riffWriter.Write(0x10);
            riffWriter.Write(isLongAudio ? 0x21 : 0x00);
            riffWriter.Write(0x00);

            // fmt chunk
            riffWriter.Write(new char[] { 'f', 'm', 't', ' ' });
            riffWriter.Write(0x12);
            riffWriter.Write((short)0x5050);
            riffWriter.Write((short)channels);
            riffWriter.Write(sampleRate);
            riffWriter.Write(128000);
            riffWriter.Write((short)0);
            riffWriter.Write((short)4);
            riffWriter.Write((short)0);

            // fact chunk
            riffWriter.Write(new char[] { 'f', 'a', 'c', 't' });
            riffWriter.Write(0x10);
            riffWriter.Write(numSamples);
            riffWriter.Write(new char[] { 'L', 'y', 'N', ' ' });
            riffWriter.Write(0x04);
            riffWriter.Write(0x0E);

            // data chunk
            riffWriter.Write(new char[] { 'd', 'a', 't', 'a' });
            riffWriter.Write(dataSize);
            riffWriter.Write(dspData);

            byte[] riffContent = riffMs.ToArray();
            int riffSize = 4 + riffContent.Length;

            int sonSize = riffSize + 0x0C;

            writer.Write(sonSize);
            writer.Write(sonSize);
            writer.Write(0);
            writer.Write(0x02);
            writer.Write(0);
            writer.Write(new char[] { 'S', 'O', 'N', '\0' });
            writer.Write(0L);

            writer.Write(new char[] { 'R', 'I', 'F', 'F' });
            writer.Write(riffSize);
            writer.Write(new char[] { 'W', 'A', 'V', 'E' });
            writer.Write(riffContent);

            writer.Write(0);

            return ms.ToArray();
        }

        private static byte[] CreateDspSon4Ch(byte[] dspData, int sampleRate, int numSamples)
        {
            int dataSize = dspData.Length;
            bool isLongAudio = numSamples > (sampleRate * 10);

            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            using var riffMs = new MemoryStream();
            using var riffWriter = new BinaryWriter(riffMs);

            // LySE chunk
            riffWriter.Write(new char[] { 'L', 'y', 'S', 'E' });
            riffWriter.Write(0x10);
            riffWriter.Write(0x01);
            riffWriter.Write(0x10);
            riffWriter.Write(isLongAudio ? 0x21 : 0x00);
            riffWriter.Write(0x00);

            // fmt chunk (WAVEFORMATEXTENSIBLE)
            riffWriter.Write(new char[] { 'f', 'm', 't', ' ' });
            riffWriter.Write(0x28);
            riffWriter.Write((short)0xFFFE);
            riffWriter.Write((short)4);
            riffWriter.Write(sampleRate);
            riffWriter.Write(128000);
            riffWriter.Write((short)0);
            riffWriter.Write((short)4);
            riffWriter.Write((short)0x16);

            riffWriter.Write((short)0);
            riffWriter.Write(0);
            riffWriter.Write(new byte[] {
                0x50, 0x50, 0x00, 0x00,
                0x00, 0x00,
                0x10, 0x00,
                0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71
            });

            // fact chunk
            riffWriter.Write(new char[] { 'f', 'a', 'c', 't' });
            riffWriter.Write(0x10);
            riffWriter.Write(numSamples);
            riffWriter.Write(new char[] { 'L', 'y', 'N', ' ' });
            riffWriter.Write(0x04);
            riffWriter.Write(0x0E);

            // data chunk
            riffWriter.Write(new char[] { 'd', 'a', 't', 'a' });
            riffWriter.Write(dataSize);
            riffWriter.Write(dspData);

            byte[] riffContent = riffMs.ToArray();
            int riffSize = 4 + riffContent.Length;

            int sonSize = riffSize + 0x0C;

            writer.Write(sonSize);
            writer.Write(sonSize);
            writer.Write(0);
            writer.Write(0x02);
            writer.Write(0);
            writer.Write(new char[] { 'S', 'O', 'N', '\0' });
            writer.Write(0L);

            writer.Write(new char[] { 'R', 'I', 'F', 'F' });
            writer.Write(riffSize);
            writer.Write(new char[] { 'W', 'A', 'V', 'E' });
            writer.Write(riffContent);

            writer.Write(0);

            return ms.ToArray();
        }

        public static string? ConvertWavToOggSns(string inputPath, string outputPath, Action<int>? progressCallback = null, bool debugMode = false, int targetSampleRate = 32000, bool forceMono = false, OutputFormat format = OutputFormat.SNS, bool normalize = true, ExtrasOption extras = ExtrasOption.None)
        {
            string? tempProcessedFile = null;

            try
            {
                if (debugMode)
                {
                    AllocConsole();
                    Console.WriteLine($"=== RGH Audio Converter - OGG ({format}) ===");
                    Console.WriteLine($"Input: {inputPath}");
                    Console.WriteLine($"Output: {outputPath}");
                    Console.WriteLine($"Target Sample Rate: {targetSampleRate}Hz");
                    Console.WriteLine($"Force Mono: {forceMono}");
                    Console.WriteLine($"Format: {format}");
                    Console.WriteLine();
                }

                progressCallback?.Invoke(0);

                string? ffmpegPath = FindExecutable("ffmpeg");
                string? oggencPath = FindExecutable("oggenc2") ?? FindExecutable("oggenc");

                if (debugMode)
                {
                    Console.WriteLine($"ffmpeg: {ffmpegPath ?? "NOT FOUND"}");
                    Console.WriteLine($"oggenc: {oggencPath ?? "NOT FOUND"}");
                    Console.WriteLine();
                }

                if (ffmpegPath == null)
                    return "ffmpeg.exe not found! Place it in the same folder as this application.";
                if (oggencPath == null)
                {
                    if (debugMode) Console.WriteLine("  oggenc not found, will use ffmpeg for OGG encoding");
                }

                byte[] checkBytes = File.ReadAllBytes(inputPath);
                int inputSampleRate = 0;
                int inputChannels = 0;
                int checkPos = 12;
                while (checkPos < checkBytes.Length - 8)
                {
                    string chunkId = System.Text.Encoding.ASCII.GetString(checkBytes, checkPos, 4);
                    int chunkSize = BitConverter.ToInt32(checkBytes, checkPos + 4);
                    if (chunkSize < 0 || checkPos + 8 + chunkSize > checkBytes.Length) break;
                    if (chunkId == "fmt ")
                    {
                        inputChannels = BitConverter.ToInt16(checkBytes, checkPos + 10);
                        inputSampleRate = BitConverter.ToInt32(checkBytes, checkPos + 12);
                        break;
                    }
                    checkPos += 8 + chunkSize;
                    if (checkPos % 2 != 0) checkPos++;
                }

                string actualInputPath = inputPath;
                bool needsProcessing = (inputSampleRate != targetSampleRate && inputSampleRate > 0) ||
                                       (forceMono && inputChannels > 1) ||
                                       normalize;

                if (needsProcessing)
                {
                    if (debugMode) Console.WriteLine($"[5%] Processing audio...");
                    progressCallback?.Invoke(5);

                    tempProcessedFile = Path.Combine(Path.GetTempPath(), $"rgh_process_{Guid.NewGuid():N}.wav");

                    string channelArg = (forceMono && inputChannels > 1) ? "-ac 1" : "";
                    string rateArg = (inputSampleRate != targetSampleRate) ? $"-ar {targetSampleRate}" : "";
                    string normalizeArg = normalize ? "-af loudnorm=I=-16:TP=-1.5:LRA=11" : "";

                    var result = RunProcess(ffmpegPath,
                        $"-y -i \"{inputPath}\" {channelArg} {rateArg} {normalizeArg} \"{tempProcessedFile}\"",
                        debugMode);

                    if (result == 0 && File.Exists(tempProcessedFile))
                    {
                        actualInputPath = tempProcessedFile;
                        if (debugMode) Console.WriteLine($"  Done");
                    }
                    else
                    {
                        if (debugMode) Console.WriteLine($"  Failed, using original");
                    }
                }

                byte[] fileBytes = File.ReadAllBytes(actualInputPath);
                int channels = 0;
                int sampleRate = 0;
                int numFrames = 0;
                int pos = 12;

                while (pos < fileBytes.Length - 8)
                {
                    string chunkId = System.Text.Encoding.ASCII.GetString(fileBytes, pos, 4);
                    int chunkSize = BitConverter.ToInt32(fileBytes, pos + 4);

                    if (chunkSize < 0 || pos + 8 + chunkSize > fileBytes.Length)
                        break;

                    if (chunkId == "fmt ")
                    {
                        channels = BitConverter.ToInt16(fileBytes, pos + 10);
                        sampleRate = BitConverter.ToInt32(fileBytes, pos + 12);
                    }
                    else if (chunkId == "data")
                    {
                        numFrames = chunkSize / (2 * channels);
                    }

                    pos += 8 + chunkSize;
                    if (pos % 2 != 0) pos++;
                }

                if (debugMode)
                {
                    Console.WriteLine($"  Channels: {channels}");
                    Console.WriteLine($"  Sample rate: {sampleRate}");
                    Console.WriteLine($"  Frames: {numFrames}");
                }

                string tempDir = Path.Combine(Path.GetTempPath(), "RGHAudioConverter_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);

                try
                {
                    byte[][] oggData = new byte[channels][];

                    for (int ch = 0; ch < channels; ch++)
                    {
                        progressCallback?.Invoke(10 + (ch * 30 / channels));

                        string monoWav = Path.Combine(tempDir, $"ch{ch}.wav");
                        string monoOgg = Path.Combine(tempDir, $"ch{ch}.ogg");

                        if (debugMode) Console.WriteLine($"[{10 + (ch * 30 / channels)}%] Channel {ch}...");

                        var ffmpegResult = RunProcess(ffmpegPath,
                            $"-y -i \"{actualInputPath}\" -filter_complex \"[0:a]pan=mono|c0=c{ch}[a]\" -map \"[a]\" \"{monoWav}\"",
                            debugMode);

                        if (ffmpegResult != 0)
                            return "ffmpeg failed to split channels";

                        progressCallback?.Invoke(20 + (ch * 30 / channels));

                        if (debugMode) Console.WriteLine($"[{20 + (ch * 30 / channels)}%] Encoding ch{ch}...");

                        int encodeResult;
                        if (oggencPath != null)
                        {
                            encodeResult = RunProcess(oggencPath,
                                $"-q 6 -o \"{monoOgg}\" \"{monoWav}\"",
                                debugMode);
                        }
                        else
                        {
                            encodeResult = RunProcess(ffmpegPath,
                                $"-y -i \"{monoWav}\" -c:a libvorbis -q:a 6 \"{monoOgg}\"",
                                debugMode);
                        }

                        if (encodeResult != 0)
                            return "OGG encoding failed";

                        byte[] rawOgg = File.ReadAllBytes(monoOgg);
                        oggData[ch] = PatchOggVendorString(rawOgg);

                        if (debugMode) Console.WriteLine($"  ch{ch}: {oggData[ch].Length} bytes");
                    }

                    progressCallback?.Invoke(70);

                    if (debugMode) Console.WriteLine("[70%] Interleaving...");

                    int interleaveSize = 0x2134;

                    int[] logicalSizes = new int[channels];
                    byte[][] paddedStreams = new byte[channels][];
                    int maxBlocks = 0;

                    for (int ch = 0; ch < channels; ch++)
                    {
                        logicalSizes[ch] = oggData[ch].Length;
                        int blocks = (oggData[ch].Length + interleaveSize - 1) / interleaveSize;
                        paddedStreams[ch] = new byte[blocks * interleaveSize];
                        Array.Copy(oggData[ch], paddedStreams[ch], oggData[ch].Length);
                        maxBlocks = Math.Max(maxBlocks, blocks);
                    }

                    for (int ch = 0; ch < channels; ch++)
                    {
                        if (paddedStreams[ch].Length < maxBlocks * interleaveSize)
                        {
                            byte[] newPadded = new byte[maxBlocks * interleaveSize];
                            Array.Copy(paddedStreams[ch], newPadded, paddedStreams[ch].Length);
                            paddedStreams[ch] = newPadded;
                        }
                    }

                    using var dataMs = new MemoryStream();
                    using var dataWriter = new BinaryWriter(dataMs);

                    dataWriter.Write(interleaveSize);
                    for (int ch = 0; ch < channels; ch++)
                        dataWriter.Write(logicalSizes[ch]);

                    for (int block = 0; block < maxBlocks; block++)
                    {
                        for (int ch = 0; ch < channels; ch++)
                        {
                            dataMs.Write(paddedStreams[ch], block * interleaveSize, interleaveSize);
                        }
                    }

                    byte[] dataPayload = dataMs.ToArray();

                    progressCallback?.Invoke(90);

                    if (debugMode) Console.WriteLine($"[90%] Building {format}...");

                    byte[] outputData;
                    if (format == OutputFormat.SON)
                    {
                        outputData = CreateOggSon(dataPayload, channels, targetSampleRate, numFrames);
                    }
                    else
                    {
                        outputData = CreateOggSns(dataPayload, channels, targetSampleRate, numFrames, extras);

                        if (extras == ExtrasOption.JustDance)
                        {
                            byte[] jdHeader = new byte[] {
                                0x4C, 0x79, 0x53, 0x45,
                                0x0C, 0x00, 0x00, 0x00,
                                0x00, 0x00, 0x00, 0x00,
                                0x0C, 0x00, 0x00, 0x00,
                                0x1F, 0x00, 0x00, 0x00
                            };
                            byte[] combined = new byte[jdHeader.Length + outputData.Length];
                            Array.Copy(jdHeader, 0, combined, 0, jdHeader.Length);
                            Array.Copy(outputData, 0, combined, jdHeader.Length, outputData.Length);
                            outputData = combined;

                            if (debugMode) Console.WriteLine("  Added Just Dance LySE header");
                        }
                    }

                    File.WriteAllBytes(outputPath, outputData);

                    progressCallback?.Invoke(100);

                    if (debugMode)
                    {
                        Console.WriteLine("[100%] Done!");
                        Console.WriteLine($"  Output: {outputData.Length} bytes");
                        Console.WriteLine();
                        Console.WriteLine("Press any key to close...");
                        Console.ReadKey();
                    }

                    return null;
                }
                finally
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
            finally
            {
                if (tempProcessedFile != null)
                {
                    try { File.Delete(tempProcessedFile); } catch { }
                }
            }
        }

        private static byte[] CreateOggSns(byte[] dataPayload, int channels, int sampleRate, int numFrames, ExtrasOption extras = ExtrasOption.None)
        {
            using var snsMs = new MemoryStream();
            using var snsWriter = new BinaryWriter(snsMs);

            int byteRate = sampleRate * channels * 2;

            byte[] fmtChunk;
            using (var fmtMs = new MemoryStream())
            using (var fmtWriter = new BinaryWriter(fmtMs))
            {
                fmtWriter.Write(new char[] { 'f', 'm', 't', ' ' });
                fmtWriter.Write(0x12);
                fmtWriter.Write((short)0x3156);
                fmtWriter.Write((short)channels);
                fmtWriter.Write(sampleRate);
                fmtWriter.Write(byteRate);
                fmtWriter.Write((short)4);
                fmtWriter.Write((short)16);
                fmtWriter.Write((short)0);
                fmtChunk = fmtMs.ToArray();
            }

            byte[] factChunk;
            using (var factMs = new MemoryStream())
            using (var factWriter = new BinaryWriter(factMs))
            {
                factWriter.Write(new char[] { 'f', 'a', 'c', 't' });
                factWriter.Write(0x10);
                factWriter.Write(numFrames);
                factWriter.Write(new char[] { 'L', 'y', 'N', ' ' });
                factWriter.Write(0x03);
                factWriter.Write(0x07);
                factChunk = factMs.ToArray();
            }

            byte[] beatChunk = Array.Empty<byte>();
            if (extras == ExtrasOption.CustomBeats)
            {
                beatChunk = GetCustomBeatData();
            }

            byte[] dataChunk;
            using (var dataChunkMs = new MemoryStream())
            using (var dataChunkWriter = new BinaryWriter(dataChunkMs))
            {
                dataChunkWriter.Write(new char[] { 'd', 'a', 't', 'a' });
                dataChunkWriter.Write(dataPayload.Length);
                dataChunkWriter.Write(dataPayload);
                dataChunk = dataChunkMs.ToArray();
            }

            int waveSize = fmtChunk.Length + factChunk.Length + beatChunk.Length + dataChunk.Length;
            snsWriter.Write(new char[] { 'R', 'I', 'F', 'F' });
            snsWriter.Write(waveSize + 4);
            snsWriter.Write(new char[] { 'W', 'A', 'V', 'E' });
            snsWriter.Write(fmtChunk);
            snsWriter.Write(factChunk);
            if (beatChunk.Length > 0)
                snsWriter.Write(beatChunk);
            snsWriter.Write(dataChunk);

            return snsMs.ToArray();
        }

        private static byte[] CreateOggSon(byte[] dataPayload, int channels, int sampleRate, int numFrames)
        {
            bool isLongAudio = numFrames > (sampleRate * 10);

            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            using var riffMs = new MemoryStream();
            using var riffWriter = new BinaryWriter(riffMs);

            int byteRate = sampleRate * channels * 2;

            // LySE chunk
            riffWriter.Write(new char[] { 'L', 'y', 'S', 'E' });
            riffWriter.Write(0x10);
            riffWriter.Write(0x01);
            riffWriter.Write(0x10);
            riffWriter.Write(isLongAudio ? 0x21 : 0x00);
            riffWriter.Write(0x00);

            // fmt chunk
            riffWriter.Write(new char[] { 'f', 'm', 't', ' ' });
            riffWriter.Write(0x12);
            riffWriter.Write((short)0x3156);
            riffWriter.Write((short)channels);
            riffWriter.Write(sampleRate);
            riffWriter.Write(byteRate);
            riffWriter.Write((short)4);
            riffWriter.Write((short)16);
            riffWriter.Write((short)0);

            // fact chunk
            riffWriter.Write(new char[] { 'f', 'a', 'c', 't' });
            riffWriter.Write(0x10);
            riffWriter.Write(numFrames);
            riffWriter.Write(new char[] { 'L', 'y', 'N', ' ' });
            riffWriter.Write(0x04);
            riffWriter.Write(0x0E);

            // data chunk
            riffWriter.Write(new char[] { 'd', 'a', 't', 'a' });
            riffWriter.Write(dataPayload.Length);
            riffWriter.Write(dataPayload);

            byte[] riffContent = riffMs.ToArray();
            int riffSize = 4 + riffContent.Length;

            int sonSize = riffSize + 0x0C;

            writer.Write(sonSize);
            writer.Write(sonSize);
            writer.Write(0);
            writer.Write(0x02);
            writer.Write(0);
            writer.Write(new char[] { 'S', 'O', 'N', '\0' });
            writer.Write(0L);

            writer.Write(new char[] { 'R', 'I', 'F', 'F' });
            writer.Write(riffSize);
            writer.Write(new char[] { 'W', 'A', 'V', 'E' });
            writer.Write(riffContent);

            writer.Write(0);

            return ms.ToArray();
        }

        // patch OGG to match SLib vendor string
        private static byte[] PatchOggVendorString(byte[] oggData)
        {
            byte[] targetVendor = System.Text.Encoding.ASCII.GetBytes("Xiph.Org libVorbis I 20050304");

            var pages = ParseOggPages(oggData);
            if (pages.Count < 2)
                return oggData;

            byte[] idPacket = pages[0].Data;
            uint serialNumber = pages[0].SerialNumber;

            var commentPackets = ExtractPacketsFromPages(pages, 1);
            if (commentPackets.Count == 0)
                return oggData;

            byte[] originalComment = commentPackets[0];

            if (originalComment.Length < 7 || originalComment[0] != 0x03)
                return oggData;

            using var newCommentMs = new MemoryStream();
            using var commentWriter = new BinaryWriter(newCommentMs);

            commentWriter.Write((byte)0x03);
            commentWriter.Write(System.Text.Encoding.ASCII.GetBytes("vorbis"));
            commentWriter.Write(targetVendor.Length);
            commentWriter.Write(targetVendor);
            commentWriter.Write(0);

            byte[] newComment = newCommentMs.ToArray();

            var setupPackets = ExtractPacketsFromPages(pages, 2);
            if (setupPackets.Count == 0)
                return oggData;

            byte[] setupPacket = setupPackets[0];

            int audioStartPage = -1;
            for (int i = 0; i < pages.Count; i++)
            {
                if ((pages[i].HeaderType & 0x01) == 0 && i > 0)
                {
                    bool isSetupPage = false;
                    if (pages[i].Data.Length > 0 && pages[i].Data[0] == 0x05)
                        isSetupPage = true;

                    if (!isSetupPage && pages[i].GranulePosition > 0)
                    {
                        audioStartPage = i;
                        break;
                    }
                }
            }

            if (audioStartPage == -1)
            {
                for (int i = 2; i < pages.Count; i++)
                {
                    if (pages[i].GranulePosition > 0)
                    {
                        audioStartPage = i;
                        break;
                    }
                }
            }

            if (audioStartPage == -1)
                audioStartPage = Math.Min(3, pages.Count);

            using var outputMs = new MemoryStream();

            // page 0: ID header
            byte[] page0 = BuildOggPage(idPacket, serialNumber, 0, 0x02, 0);
            outputMs.Write(page0);

            // page 1: comment + setup
            int maxSegments = 15;
            int commentSegments = (newComment.Length + 254) / 255;
            int setupSegmentsAvailable = maxSegments - commentSegments;

            int setupBytesInPage1 = setupSegmentsAvailable * 255;
            if (setupBytesInPage1 > setupPacket.Length)
                setupBytesInPage1 = setupPacket.Length;

            using var page1DataMs = new MemoryStream();
            page1DataMs.Write(newComment);
            page1DataMs.Write(setupPacket, 0, setupBytesInPage1);
            byte[] page1Data = page1DataMs.ToArray();

            byte[] segmentTable1 = BuildSegmentTableForPackets(newComment.Length, setupBytesInPage1, setupBytesInPage1 < setupPacket.Length);
            byte[] page1 = BuildOggPageWithSegmentTable(page1Data, serialNumber, 1, 0x00, 0, segmentTable1);
            outputMs.Write(page1);

            // continuation pages for setup
            int setupRemaining = setupPacket.Length - setupBytesInPage1;
            int setupOffset = setupBytesInPage1;
            int pageSequence = 2;

            while (setupRemaining > 0)
            {
                int chunkSize = Math.Min(setupRemaining, 255 * 255);
                byte[] chunk = new byte[chunkSize];
                Array.Copy(setupPacket, setupOffset, chunk, 0, chunkSize);

                bool isLastSetupPage = (setupOffset + chunkSize >= setupPacket.Length);
                byte headerType = 0x01;

                byte[] contPage = BuildOggPage(chunk, serialNumber, pageSequence, headerType, 0, !isLastSetupPage);
                outputMs.Write(contPage);

                setupOffset += chunkSize;
                setupRemaining -= chunkSize;
                pageSequence++;
            }

            // audio pages
            for (int i = audioStartPage; i < pages.Count; i++)
            {
                var page = pages[i];
                byte[] rebuiltPage = BuildOggPageRaw(page.Data, serialNumber, pageSequence, page.HeaderType, page.GranulePosition, page.SegmentTable);
                outputMs.Write(rebuiltPage);
                pageSequence++;
            }

            return outputMs.ToArray();
        }

        private static byte[] BuildSegmentTableForPackets(int commentLen, int setupLen, bool setupContinues)
        {
            var segments = new List<byte>();

            int remaining = commentLen;
            while (remaining >= 255)
            {
                segments.Add(255);
                remaining -= 255;
            }
            segments.Add((byte)remaining);

            remaining = setupLen;
            while (remaining >= 255)
            {
                segments.Add(255);
                remaining -= 255;
            }
            if (setupContinues)
            {
                segments.Add(255);
            }
            else
            {
                segments.Add((byte)remaining);
            }

            return segments.ToArray();
        }

        private static byte[] BuildOggPageWithSegmentTable(byte[] data, uint serialNumber, int pageSequence, byte headerType, long granulePosition, byte[] segmentTable)
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            writer.Write(System.Text.Encoding.ASCII.GetBytes("OggS"));
            writer.Write((byte)0);
            writer.Write(headerType);
            writer.Write(granulePosition);
            writer.Write(serialNumber);
            writer.Write(pageSequence);
            writer.Write(0);
            writer.Write((byte)segmentTable.Length);
            writer.Write(segmentTable);
            writer.Write(data);

            byte[] page = ms.ToArray();

            uint crc = CalculateOggCrc(page);
            page[22] = (byte)(crc & 0xFF);
            page[23] = (byte)((crc >> 8) & 0xFF);
            page[24] = (byte)((crc >> 16) & 0xFF);
            page[25] = (byte)((crc >> 24) & 0xFF);

            return page;
        }

        private static List<byte[]> ExtractPacketsFromPages(List<OggPage> pages, int startPage)
        {
            var packets = new List<byte[]>();
            var currentPacket = new MemoryStream();

            for (int p = startPage; p < pages.Count; p++)
            {
                var page = pages[p];
                int dataOffset = 0;

                for (int s = 0; s < page.SegmentTable.Length; s++)
                {
                    int segSize = page.SegmentTable[s];

                    if (dataOffset + segSize <= page.Data.Length)
                    {
                        currentPacket.Write(page.Data, dataOffset, segSize);
                    }
                    dataOffset += segSize;

                    if (segSize < 255)
                    {
                        packets.Add(currentPacket.ToArray());
                        currentPacket = new MemoryStream();

                        if (packets.Count >= 1 && startPage == 1)
                            return packets;
                        if (packets.Count >= 1 && startPage == 2)
                            return packets;
                    }
                }
            }

            if (currentPacket.Length > 0)
            {
                packets.Add(currentPacket.ToArray());
            }

            return packets;
        }

        private class OggPage
        {
            public byte HeaderType;
            public long GranulePosition;
            public uint SerialNumber;
            public int PageSequence;
            public byte[] SegmentTable = Array.Empty<byte>();
            public byte[] Data = Array.Empty<byte>();
        }

        private static List<OggPage> ParseOggPages(byte[] data)
        {
            var pages = new List<OggPage>();
            int pos = 0;

            while (pos + 27 <= data.Length)
            {
                if (data[pos] != 'O' || data[pos + 1] != 'g' || data[pos + 2] != 'g' || data[pos + 3] != 'S')
                    break;

                var page = new OggPage
                {
                    HeaderType = data[pos + 5],
                    GranulePosition = BitConverter.ToInt64(data, pos + 6),
                    SerialNumber = BitConverter.ToUInt32(data, pos + 14),
                    PageSequence = BitConverter.ToInt32(data, pos + 18)
                };

                int numSegments = data[pos + 26];
                if (pos + 27 + numSegments > data.Length)
                    break;

                page.SegmentTable = new byte[numSegments];
                Array.Copy(data, pos + 27, page.SegmentTable, 0, numSegments);

                int dataSize = 0;
                foreach (var seg in page.SegmentTable)
                    dataSize += seg;

                int headerSize = 27 + numSegments;
                if (pos + headerSize + dataSize > data.Length)
                    break;

                page.Data = new byte[dataSize];
                Array.Copy(data, pos + headerSize, page.Data, 0, dataSize);

                pages.Add(page);
                pos += headerSize + dataSize;
            }

            return pages;
        }

        private static byte[] BuildOggPage(byte[] data, uint serialNumber, int pageSequence, byte headerType, long granulePosition, bool continuesNext = false)
        {
            var segments = new List<byte>();
            int remaining = data.Length;

            while (remaining >= 255)
            {
                segments.Add(255);
                remaining -= 255;
            }

            if (continuesNext && remaining == 0)
            {
                // no final segment needed
            }
            else
            {
                segments.Add((byte)remaining);
            }

            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            writer.Write(System.Text.Encoding.ASCII.GetBytes("OggS"));
            writer.Write((byte)0);
            writer.Write(headerType);
            writer.Write(granulePosition);
            writer.Write(serialNumber);
            writer.Write(pageSequence);
            writer.Write(0);
            writer.Write((byte)segments.Count);
            writer.Write(segments.ToArray());
            writer.Write(data);

            byte[] page = ms.ToArray();

            uint crc = CalculateOggCrc(page);
            page[22] = (byte)(crc & 0xFF);
            page[23] = (byte)((crc >> 8) & 0xFF);
            page[24] = (byte)((crc >> 16) & 0xFF);
            page[25] = (byte)((crc >> 24) & 0xFF);

            return page;
        }

        private static byte[] BuildOggPageRaw(byte[] data, uint serialNumber, int pageSequence, byte headerType, long granulePosition, byte[] segmentTable)
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            writer.Write(System.Text.Encoding.ASCII.GetBytes("OggS"));
            writer.Write((byte)0);
            writer.Write(headerType);
            writer.Write(granulePosition);
            writer.Write(serialNumber);
            writer.Write(pageSequence);
            writer.Write(0);
            writer.Write((byte)segmentTable.Length);
            writer.Write(segmentTable);
            writer.Write(data);

            byte[] page = ms.ToArray();

            uint crc = CalculateOggCrc(page);
            page[22] = (byte)(crc & 0xFF);
            page[23] = (byte)((crc >> 8) & 0xFF);
            page[24] = (byte)((crc >> 16) & 0xFF);
            page[25] = (byte)((crc >> 24) & 0xFF);

            return page;
        }

        private static readonly uint[] CrcTable = InitCrcTable();

        private static uint[] InitCrcTable()
        {
            uint[] table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint crc = i << 24;
                for (int j = 0; j < 8; j++)
                {
                    if ((crc & 0x80000000) != 0)
                        crc = (crc << 1) ^ 0x04C11DB7;
                    else
                        crc <<= 1;
                }
                table[i] = crc;
            }
            return table;
        }

        private static uint CalculateOggCrc(byte[] data)
        {
            uint crc = 0;
            foreach (byte b in data)
            {
                crc = (crc << 8) ^ CrcTable[(crc >> 24) ^ b];
            }
            return crc;
        }

        private static string? FindExecutable(string name)
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;

            string[] extensions = { "", ".exe" };
            foreach (var ext in extensions)
            {
                string path = Path.Combine(baseDir, name + ext);
                if (File.Exists(path))
                    return path;
            }

            string? pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (pathEnv != null)
            {
                foreach (var dir in pathEnv.Split(Path.PathSeparator))
                {
                    foreach (var ext in extensions)
                    {
                        string path = Path.Combine(dir, name + ext);
                        if (File.Exists(path))
                            return path;
                    }
                }
            }

            return null;
        }

        private static int RunProcess(string fileName, string arguments, bool showOutput = false)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = !showOutput,
                RedirectStandardOutput = showOutput,
                RedirectStandardError = showOutput
            };

            using var process = Process.Start(psi);

            if (process == null)
                return -1;

            if (showOutput)
            {
                process.OutputDataReceived += (s, e) => { if (e.Data != null) Console.WriteLine(e.Data); };
                process.ErrorDataReceived += (s, e) => { if (e.Data != null) Console.WriteLine(e.Data); };
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }

            process.WaitForExit();
            return process.ExitCode;
        }
    }
}
