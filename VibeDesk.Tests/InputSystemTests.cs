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
    }
}
