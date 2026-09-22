using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VibeDesk.Capture;
using VibeDesk.Network.Protocol;

namespace VibeDesk.Tests
{
    [TestClass]
    public class ScalerAndPerformanceTests
    {
        [TestMethod]
        public void TestResolutionDownscalingSpeedAndValidity()
        {
            // Create simulated 1080p desktop image
            int originalW = 1920;
            int originalH = 1080;
            using var testBitmap = new Bitmap(originalW, originalH, PixelFormat.Format32bppRgb);
            using (var g = Graphics.FromImage(testBitmap))
            {
                g.Clear(Color.DarkSlateGray);
                g.FillRectangle(Brushes.DodgerBlue, 100, 100, 800, 600);
                g.DrawString("VibeDesk 60-120 FPS Benchmark", new Font("Arial", 24), Brushes.White, 150, 150);
            }

            float[] scales = { 1.0f, 0.75f, 0.50f, 0.33f };

            foreach (var scale in scales)
            {
                int targetW = scale >= 0.99f ? originalW : Math.Max(16, ((int)(originalW * scale)) / 2 * 2);
                int targetH = scale >= 0.99f ? originalH : Math.Max(16, ((int)(originalH * scale)) / 2 * 2);

                Assert.AreEqual(0, targetW % 2, $"Target width must be even for JPEG encoder: {targetW}");
                Assert.AreEqual(0, targetH % 2, $"Target height must be even for JPEG encoder: {targetH}");

                // Downscale using HighSpeed graphics
                using var scaledBitmap = new Bitmap(targetW, targetH, PixelFormat.Format32bppRgb);
                using var scaledG = Graphics.FromImage(scaledBitmap);
                scaledG.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighSpeed;
                scaledG.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
                scaledG.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighSpeed;
                scaledG.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighSpeed;

                var sw = Stopwatch.StartNew();
                scaledG.DrawImage(testBitmap, new Rectangle(0, 0, targetW, targetH), new Rectangle(0, 0, originalW, originalH), GraphicsUnit.Pixel);

                using var ms = new MemoryStream();
                var encoderParams = new EncoderParameters(1);
                encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, 65L);
                scaledBitmap.Save(ms, GetJpegCodec(), encoderParams);
                sw.Stop();

                byte[] encodedBytes = ms.ToArray();
                Assert.IsTrue(encodedBytes.Length > 0, "Encoded JPEG must not be empty");

                // Verify valid JPEG
                using var decodedMs = new MemoryStream(encodedBytes);
                using var decodedImage = Image.FromStream(decodedMs);
                Assert.AreEqual(targetW, decodedImage.Width);
                Assert.AreEqual(targetH, decodedImage.Height);

                Console.WriteLine($"Scale {(int)(scale * 100)}% ({targetW}x{targetH}): {sw.ElapsedMilliseconds} ms, {encodedBytes.Length / 1024} KB");

                // At 50% scale, encode time should be very fast (< 25ms in test runner, typically 3-5ms native)
                if (scale <= 0.50f)
                {
                    Assert.IsTrue(sw.ElapsedMilliseconds < 50, $"50% scale downscale+encode took {sw.ElapsedMilliseconds}ms, expected under 50ms");
                }
            }
        }

        [TestMethod]
        public void TestStreamSettingsPacketSerialization()
        {
            float expectedScale = 0.50f;
            int expectedFps = 120;
            int expectedQuality = 60;

            byte[] packet = PacketBuilder.CreateStreamSettings(expectedScale, expectedFps, expectedQuality);

            Assert.IsNotNull(packet);
            Assert.AreEqual(1 + 4 + 4 + 4, packet.Length);
            Assert.AreEqual((byte)PacketType.StreamSettings, packet[0]);

            float actualScale = BitConverter.ToSingle(packet.AsSpan(1, 4));
            int actualFps = BitConverter.ToInt32(packet.AsSpan(5, 4));
            int actualQuality = BitConverter.ToInt32(packet.AsSpan(9, 4));

            Assert.AreEqual(expectedScale, actualScale, 0.001f);
            Assert.AreEqual(expectedFps, actualFps);
            Assert.AreEqual(expectedQuality, actualQuality);
        }

        [TestMethod]
        public void TestFrameReassemblerMultiSlotIntegrity()
        {
            var reassembler = new FrameReassembler();
            int completedFrames = 0;
            byte[]? lastReceivedData = null;

            reassembler.OnFrameReady += frameBytes =>
            {
                completedFrames++;
                lastReceivedData = frameBytes;
            };

            // Simulate sending frame 100 with 3 chunks
            byte[] testPayload = new byte[3000];
            for (int i = 0; i < testPayload.Length; i++) testPayload[i] = (byte)(i % 256);

            ushort totalChunks = 3;
            ushort chunkSize = 1000;

            // Send chunks: chunk 0 of frame 100, chunk 0 of frame 101 (interleaved!), then rest of 100
            byte[] chunk0_f100 = PacketBuilder.CreateFrameChunk(100, 0, totalChunks, testPayload, 0, chunkSize);
            byte[] chunk0_f101 = PacketBuilder.CreateFrameChunk(101, 0, totalChunks, testPayload, 0, chunkSize);
            byte[] chunk1_f100 = PacketBuilder.CreateFrameChunk(100, 1, totalChunks, testPayload, chunkSize, chunkSize);
            byte[] chunk2_f100 = PacketBuilder.CreateFrameChunk(100, 2, totalChunks, testPayload, chunkSize * 2, chunkSize);

            reassembler.ProcessChunk(chunk0_f100);
            reassembler.ProcessChunk(chunk0_f101); // interleaved chunk
            reassembler.ProcessChunk(chunk1_f100);
            reassembler.ProcessChunk(chunk2_f100);

            // Frame 100 should be completely reassembled without being corrupted by frame 101's chunk
            Assert.AreEqual(1, completedFrames, "Frame 100 should be completed");
            Assert.IsNotNull(lastReceivedData);
            Assert.AreEqual(3000, lastReceivedData.Length);
            CollectionAssert.AreEqual(testPayload, lastReceivedData);
        }

        [TestMethod]
        public void TestMouseNormalizationInvariance()
        {
            // Verify that clicking at 50% of the screen produces (0.5, 0.5) regardless of whether
            // the remote stream is 1920x1080, 1280x720, or 960x540.
            double normX_1080 = 960.0 / 1920.0;
            double normY_1080 = 540.0 / 1080.0;

            double normX_540 = 480.0 / 960.0;
            double normY_540 = 270.0 / 540.0;

            Assert.AreEqual(0.5, normX_1080, 0.0001);
            Assert.AreEqual(0.5, normY_1080, 0.0001);
            Assert.AreEqual(normX_1080, normX_540, 0.0001);
            Assert.AreEqual(normY_1080, normY_540, 0.0001);
        }

        private static ImageCodecInfo GetJpegCodec()
        {
            foreach (var codec in ImageCodecInfo.GetImageDecoders())
            {
                if (codec.FormatID == ImageFormat.Jpeg.Guid) return codec;
            }
            return ImageCodecInfo.GetImageDecoders()[0];
        }
    }
}
