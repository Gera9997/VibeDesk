#pragma warning disable CS0618
using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VibeDesk.Input;
using VibeDesk.Network.Protocol;

namespace VibeDesk.Tests
{
    [TestClass]
    public class InputSystemTests
    {
        [TestMethod]
        public void TestCreateMouseRelativePacket()
        {
            int dx = 42;
            int dy = -99;
            byte[] packet = PacketBuilder.CreateMouseRelative(dx, dy);

            Assert.IsNotNull(packet);
            Assert.AreEqual(6, packet.Length);
            Assert.AreEqual((byte)PacketType.InputEvent, packet[0]);
            Assert.AreEqual((byte)InputEventType.MouseRelative, packet[1]);

            short readDx = BitConverter.ToInt16(packet, 2);
            short readDy = BitConverter.ToInt16(packet, 4);

            Assert.AreEqual((short)dx, readDx);
            Assert.AreEqual((short)dy, readDy);
        }

        [TestMethod]
        public void TestMouseRelativeClamping()
        {
            // Verify boundaries
            byte[] minPacket = PacketBuilder.CreateMouseRelative(-40000, 50000);
            short readMin = BitConverter.ToInt16(minPacket, 2);
            short readMax = BitConverter.ToInt16(minPacket, 4);

            Assert.AreEqual(short.MinValue, readMin);
            Assert.AreEqual(short.MaxValue, readMax);
        }

        [TestMethod]
        public void TestCreateMouseButtonXButtons()
        {
            // Mouse 4 (XButton1) Down
            byte[] pDown1 = PacketBuilder.CreateMouseButton(VibeMouseButton.XButton1, true);
            Assert.AreEqual(3, pDown1.Length);
            Assert.AreEqual((byte)PacketType.InputEvent, pDown1[0]);
            Assert.AreEqual((byte)InputEventType.MouseDown, pDown1[1]);
            Assert.AreEqual((byte)VibeMouseButton.XButton1, pDown1[2]);

            // Mouse 5 (XButton2) Up
            byte[] pUp2 = PacketBuilder.CreateMouseButton(VibeMouseButton.XButton2, false);
            Assert.AreEqual(3, pUp2.Length);
            Assert.AreEqual((byte)PacketType.InputEvent, pUp2[0]);
            Assert.AreEqual((byte)InputEventType.MouseUp, pUp2[1]);
            Assert.AreEqual((byte)VibeMouseButton.XButton2, pUp2[2]);
        }

        [TestMethod]
        public void TestKeyPacketWithWindowsAndCapsLockKeys()
        {
            // VK_LWIN = 0x5B
            byte[] winPacket = PacketBuilder.CreateKey(0x5B, true);
            Assert.AreEqual(5, winPacket.Length);
            Assert.AreEqual((byte)PacketType.InputEvent, winPacket[0]);
            Assert.AreEqual((byte)InputEventType.KeyDown, winPacket[1]);
            Assert.AreEqual((ushort)0x5B, BitConverter.ToUInt16(winPacket, 2));
            Assert.AreEqual((byte)1, winPacket[4]);

            // VK_CAPITAL = 0x14 Up
            byte[] capsPacket = PacketBuilder.CreateKey(0x14, false);
            Assert.AreEqual((byte)PacketType.InputEvent, capsPacket[0]);
            Assert.AreEqual((byte)InputEventType.KeyUp, capsPacket[1]);
            Assert.AreEqual((ushort)0x14, BitConverter.ToUInt16(capsPacket, 2));
            Assert.AreEqual((byte)0, capsPacket[4]);
        }

        [TestMethod]
        public void TestCreateScreenInfoPacketWithMonitors()
        {
            byte[] packet = PacketBuilder.CreateScreenInfo(2560, 1440, 2, 1);
            Assert.AreEqual(11, packet.Length);
            Assert.AreEqual((byte)PacketType.ScreenInfo, packet[0]);
            Assert.AreEqual(2560, BitConverter.ToInt32(packet, 1));
            Assert.AreEqual(1440, BitConverter.ToInt32(packet, 5));
            Assert.AreEqual((byte)2, packet[9]);
            Assert.AreEqual((byte)1, packet[10]);
        }

        [TestMethod]
        public void TestCreateSwitchMonitorPacket()
        {
            byte[] packet = PacketBuilder.CreateSwitchMonitor(2);
            Assert.AreEqual(2, packet.Length);
            Assert.AreEqual((byte)PacketType.SwitchMonitor, packet[0]);
            Assert.AreEqual((byte)2, packet[1]);
        }

        [TestMethod]
        public void TestOpusEncodeDecode()
        {
            var encoder = new Concentus.Structs.OpusEncoder(48000, 2, Concentus.Enums.OpusApplication.OPUS_APPLICATION_AUDIO);
            encoder.Bitrate = 64000;
            short[] inPcm = new short[960 * 2]; // 20ms of stereo 48kHz
            byte[] outOpus = new byte[1000];
            int encodedBytes = encoder.Encode(inPcm, 0, 960, outOpus, 0, outOpus.Length);
            Assert.IsTrue(encodedBytes > 0);

            var decoder = new Concentus.Structs.OpusDecoder(48000, 2);
            short[] outPcm = new short[960 * 2];
            int decodedSamples = decoder.Decode(outOpus, 0, encodedBytes, outPcm, 0, 960, false);
            Assert.AreEqual(960, decodedSamples);
        }

        [TestMethod]
        public void TestCreateAudioChunkPacket()
        {
            byte[] fakeOpus = new byte[] { 0x12, 0x34, 0x56, 0x78 };
            byte[] packet = PacketBuilder.CreateAudioChunk(100, fakeOpus, 0, fakeOpus.Length);

            Assert.IsNotNull(packet);
            Assert.AreEqual(9, packet.Length);
            Assert.AreEqual((byte)PacketType.AudioChunk, packet[0]);
            Assert.AreEqual(100u, BitConverter.ToUInt32(packet, 1));
            Assert.AreEqual(0x12, packet[5]);
            Assert.AreEqual(0x78, packet[8]);
        }

        [TestMethod]
        public void TestCreateAudioControlPacket()
        {
            byte[] pEnabled = PacketBuilder.CreateAudioControl(true);
            Assert.AreEqual(2, pEnabled.Length);
            Assert.AreEqual((byte)PacketType.AudioControl, pEnabled[0]);
            Assert.AreEqual(1, pEnabled[1]);

            byte[] pDisabled = PacketBuilder.CreateAudioControl(false);
            Assert.AreEqual(2, pDisabled.Length);
            Assert.AreEqual((byte)PacketType.AudioControl, pDisabled[0]);
            Assert.AreEqual(0, pDisabled[1]);
        }
    }
}
