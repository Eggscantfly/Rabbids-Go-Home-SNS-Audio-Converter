using System;
using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RGHAudioConverter
{
    public static class LynEncoder
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AllocConsole();

        // DSP Coefs From VGMstream

        private static readonly short[] DSP_COEFS = 
        {
            1195, -787,
            1929, -289,
            2466, -1307,
            3216, -1343,
            2125, -1372,
            2434, -521,
            2806, -1286,
            3046, -1035
        };

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

        public static string? ConvertWavToDspSns(string inputPath, string outputPath, Action<int>? progressCallback = null, bool debugMode = false, int targetSampleRate = 32000, bool forceMono = false)
        {
            string? tempProcessedFile = null;
            
            try
            {
                if (debugMode)
                {
                    AllocConsole();
                    Console.WriteLine("=== RGH Audio Converter - DSP Encoding ===");
                    Console.WriteLine($"Input: {inputPath}");
                    Console.WriteLine($"Output: {outputPath}");
                    Console.WriteLine($"Target Sample Rate: {targetSampleRate}Hz");
                    Console.WriteLine($"Force Mono: {forceMono}");
                    Console.WriteLine();
                }

                progressCallback?.Invoke(0);
                
                string? ffmpegPath = FindExecutable("ffmpeg");
                
                // Check input sample rate and channels
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
                                       (forceMono && inputChannels > 1);
                
                if (needsProcessing && ffmpegPath != null)
                {
                    if (debugMode) Console.WriteLine($"[5%] Processing audio (resample/mono)...");
                    progressCallback?.Invoke(5);
                    
                    tempProcessedFile = Path.Combine(Path.GetTempPath(), $"rgh_process_{Guid.NewGuid():N}.wav");
                    
                    string channelArg = (forceMono && inputChannels > 1) ? "-ac 1" : "";
                    string rateArg = (inputSampleRate != targetSampleRate) ? $"-ar {targetSampleRate}" : "";
                    
                    var result = RunProcess(ffmpegPath, 
                        $"-y -i \"{inputPath}\" {channelArg} {rateArg} \"{tempProcessedFile}\"",
                        debugMode);
                    
                    if (result == 0 && File.Exists(tempProcessedFile))
                    {
                        actualInputPath = tempProcessedFile;
                        if (debugMode) Console.WriteLine($"  Processed successfully");
                    }
                    else
                    {
                        if (debugMode) Console.WriteLine($"  Warning: Processing failed, using original");
                    }
                }
                else if (needsProcessing && ffmpegPath == null)
                {
                    if (debugMode) Console.WriteLine($"  Warning: ffmpeg not found, cannot resample/convert to mono");
                }
                
                if (debugMode) Console.WriteLine("[10%] Reading WAV file...");
                
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
                        if (debugMode) Console.WriteLine($"  Warning: Invalid chunk '{chunkId}' size {chunkSize}");
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

                if (channels == 1)
                {
                    if (debugMode) Console.WriteLine("  Encoding mono...");
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

                    if (debugMode) Console.WriteLine("  Encoding left channel...");
                    byte[] leftEncoded = EncodeDspData(leftSamples, progressCallback, 20, 55);
                    
                    if (debugMode) Console.WriteLine("  Encoding right channel...");
                    byte[] rightEncoded = EncodeDspData(rightSamples, progressCallback, 55, 90);
                    
                    if (debugMode) Console.WriteLine("  Interleaving...");
                    dspData = InterleaveDspChannels(leftEncoded, rightEncoded);
                }

                progressCallback?.Invoke(95);

                if (debugMode) Console.WriteLine("[95%] Building SNS...");

                byte[] snsData = CreateDspSns(dspData, channels, targetSampleRate, numFrames);

                File.WriteAllBytes(outputPath, snsData);
                
                progressCallback?.Invoke(100);

                if (debugMode)
                {
                    Console.WriteLine("[100%] Done!");
                    Console.WriteLine($"  Output: {snsData.Length} bytes");
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

                // Try each coefficient and find the one with lowest error
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

                    // Calculate residuals to find max for scale
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

                    // Find optimal scale (need to fit residuals in -8 to 7 range)
                    int scale = 0;
                    while (scale < 12 && maxResidual > ((1 << scale) * 8 - 1))
                        scale++;

                    int scaleFactor = 1 << scale;

                    // Encode with feedback and calculate total error
                    int[] testNibbles = new int[14];
                    int testH1 = hist1, testH2 = hist2;
                    long totalError = 0;

                    for (int i = 0; i < 14; i++)
                    {
                        int pred = (c1 * testH1 + c2 * testH2 + 1024) >> 11;
                        int residual = frameSamples[i] - pred;

                        // Quantize residual to nibble
                        int nibble = (residual + (scaleFactor >> 1)) / scaleFactor;
                        nibble = Math.Clamp(nibble, -8, 7);
                        testNibbles[i] = nibble & 0xF;

                        // Decode to get actual reconstructed sample
                        int signedNibble = nibble >= 8 ? nibble - 16 : nibble;
                        int decoded = signedNibble * scaleFactor;
                        decoded = ((decoded << 11) + 1024 + c1 * testH1 + c2 * testH2) >> 11;
                        decoded = Math.Clamp(decoded, -32768, 32767);

                        // Track error
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

                // Write the frame
                result[frameOffset] = (byte)((bestCoef << 4) | bestScale);
                
                for (int i = 0; i < 7; i++)
                {
                    result[frameOffset + 1 + i] = (byte)((bestNibbles[i * 2] << 4) | bestNibbles[i * 2 + 1]);
                }

                // Update history with best results
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

        private static byte[] CreateDspSns(byte[] dspData, int channels, int sampleRate, int numSamples)
        {
            int dataSize = dspData.Length;
            int fmtSize = 0x12;
            int factSize = 0x10;

            int blockAlign = 4;
            int bitsPerSample = 4;
            int avgBytesPerSec = 128000;

            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            int riffSize = 4 + (8 + fmtSize) + (8 + factSize) + (8 + dataSize);

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

            writer.Write(new char[] { 'd', 'a', 't', 'a' });
            writer.Write(dataSize);
            writer.Write(dspData);

            return ms.ToArray();
        }

        public static string? ConvertWavToOggSns(string inputPath, string outputPath, Action<int>? progressCallback = null, bool debugMode = false, int targetSampleRate = 32000, bool forceMono = false)
        {
            string? tempProcessedFile = null;
            
            try
            {
                if (debugMode)
                {
                    AllocConsole();
                    Console.WriteLine("=== RGH Audio Converter - OGG Encoding ===");
                    Console.WriteLine($"Input: {inputPath}");
                    Console.WriteLine($"Output: {outputPath}");
                    Console.WriteLine($"Target Sample Rate: {targetSampleRate}Hz");
                    Console.WriteLine($"Force Mono: {forceMono}");
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
                    return "oggenc2.exe not found! Place it in the same folder as this application.";

                // Check input sample rate and channels
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
                                       (forceMono && inputChannels > 1);
                
                if (needsProcessing)
                {
                    if (debugMode) Console.WriteLine($"[5%] Processing audio (resample/mono)...");
                    progressCallback?.Invoke(5);
                    
                    tempProcessedFile = Path.Combine(Path.GetTempPath(), $"rgh_process_{Guid.NewGuid():N}.wav");
                    
                    string channelArg = (forceMono && inputChannels > 1) ? "-ac 1" : "";
                    string rateArg = (inputSampleRate != targetSampleRate) ? $"-ar {targetSampleRate}" : "";
                    
                    var result = RunProcess(ffmpegPath, 
                        $"-y -i \"{inputPath}\" {channelArg} {rateArg} \"{tempProcessedFile}\"",
                        debugMode);
                    
                    if (result == 0 && File.Exists(tempProcessedFile))
                    {
                        actualInputPath = tempProcessedFile;
                        if (debugMode) Console.WriteLine($"  Processed successfully");
                    }
                    else
                    {
                        if (debugMode) Console.WriteLine($"  Warning: Processing failed, using original");
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

                        if (debugMode) Console.WriteLine($"[{10 + (ch * 30 / channels)}%] Extracting channel {ch}...");

                        var ffmpegResult = RunProcess(ffmpegPath, 
                            $"-y -i \"{actualInputPath}\" -filter_complex \"[0:a]pan=mono|c0=c{ch}[a]\" -map \"[a]\" \"{monoWav}\"",
                            debugMode);
                        
                        if (ffmpegResult != 0)
                            return "ffmpeg failed to split channels";

                        progressCallback?.Invoke(20 + (ch * 30 / channels));

                        if (debugMode) Console.WriteLine($"[{20 + (ch * 30 / channels)}%] Encoding channel {ch}...");

                        var oggResult = RunProcess(oggencPath,
                            $"-q 6 --comment \"ENCODER=SLib_encoder\" -o \"{monoOgg}\" \"{monoWav}\"",
                            debugMode);

                        if (oggResult != 0)
                            return "oggenc failed to encode";

                        oggData[ch] = File.ReadAllBytes(monoOgg);
                        
                        if (debugMode) Console.WriteLine($"  Channel {ch}: {oggData[ch].Length} bytes");
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

                    using var ms = new MemoryStream();
                    for (int block = 0; block < maxBlocks; block++)
                    {
                        for (int ch = 0; ch < channels; ch++)
                        {
                            ms.Write(paddedStreams[ch], block * interleaveSize, interleaveSize);
                        }
                    }
                    byte[] interleaved = ms.ToArray();

                    using var dataMs = new MemoryStream();
                    using var dataWriter = new BinaryWriter(dataMs);
                    dataWriter.Write(interleaveSize);
                    for (int ch = 0; ch < channels; ch++)
                        dataWriter.Write(logicalSizes[ch]);
                    dataWriter.Write(interleaved);
                    byte[] dataPayload = dataMs.ToArray();

                    using var snsMs = new MemoryStream();
                    using var snsWriter = new BinaryWriter(snsMs);

                    int byteRate = targetSampleRate * channels * 2;

                    byte[] fmtChunk;
                    using (var fmtMs = new MemoryStream())
                    using (var fmtWriter = new BinaryWriter(fmtMs))
                    {
                        fmtWriter.Write(new char[] { 'f', 'm', 't', ' ' });
                        fmtWriter.Write(0x12);
                        fmtWriter.Write((short)0x3156);
                        fmtWriter.Write((short)channels);
                        fmtWriter.Write(targetSampleRate);
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

                    byte[] dataChunk;
                    using (var dataChunkMs = new MemoryStream())
                    using (var dataChunkWriter = new BinaryWriter(dataChunkMs))
                    {
                        dataChunkWriter.Write(new char[] { 'd', 'a', 't', 'a' });
                        dataChunkWriter.Write(dataPayload.Length);
                        dataChunkWriter.Write(dataPayload);
                        dataChunk = dataChunkMs.ToArray();
                    }

                    int waveSize = fmtChunk.Length + factChunk.Length + dataChunk.Length;
                    snsWriter.Write(new char[] { 'R', 'I', 'F', 'F' });
                    snsWriter.Write(waveSize + 4);
                    snsWriter.Write(new char[] { 'W', 'A', 'V', 'E' });
                    snsWriter.Write(fmtChunk);
                    snsWriter.Write(factChunk);
                    snsWriter.Write(dataChunk);

                    File.WriteAllBytes(outputPath, snsMs.ToArray());
                    
                    progressCallback?.Invoke(100);
                    
                    if (debugMode)
                    {
                        Console.WriteLine("[100%] Done!");
                        Console.WriteLine($"  Output: {snsMs.Length} bytes");
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
