using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using PurrNet;
using PurrNet.Modules;
using PurrNet.Packing;
using PurrNet.Utils;
using UnityEngine;
using UnityEngine.TestTools;

public class DeltaModuleBaselineResyncTests
{
    const uint KeyHash = 4711;

    static readonly Regex MissingBaselineLog = new Regex("Delta baseline \\d+ missing");

    [OneTimeSetUp]
    public void Init()
    {
        NetworkManager.LoadOrGenerateHashes();
    }

    static PlayerID Player(int i) => new PlayerID((ulong)i, false);

    sealed class Wire<T>
    {
        public readonly DeltaModule sender = new DeltaModule(null, null);
        public DeltaModule receiver = new DeltaModule(null, null);
        public readonly PlayerID target = Player(2);
        public uint lastReceivedId;
        private readonly Dictionary<uint, T> _history = new();

        public BitPacker Write(T value)
        {
            var packer = BitPackerPool.Get();
            PackedUInt cachedKey = default;
            sender.Write(packer, target, KeyHash, value, ref cachedKey);
            packer.ResetPositionAndMode(true);
            return packer;
        }

        public static uint PeekBaseline(BitPacker packer)
        {
            int pos = packer.positionInBits;
            PackedUInt baseline = default;
            DeltaPacker<PackedUInt>.Read(packer, default, ref baseline);
            packer.SetBitPosition(pos);
            return baseline.value;
        }

        public uint PeekValueId(BitPacker packer)
        {
            int pos = packer.positionInBits;
            PackedUInt baseline = default;
            DeltaPacker<PackedUInt>.Read(packer, default, ref baseline);
            bool changed = false;
            Packer<bool>.Read(packer, ref changed);
            uint id = 0;
            if (changed)
            {
                T old = baseline.value != 0 ? _history[baseline.value] : default;
                T decoded = default;
                DeltaPacker<T>.Read(packer, old, ref decoded);
                PackedUInt valueId = default;
                DeltaPacker<PackedUInt>.Read(packer, baseline, ref valueId);
                id = valueId.value;
                _history[id] = decoded;
            }
            packer.SetBitPosition(pos);
            return id;
        }

        public T Read(BitPacker packer)
        {
            T value = default;
            PackedUInt cachedKey = default;
            receiver.Read(packer, KeyHash, PlayerID.Server, ref value, ref cachedKey);
            return value;
        }

        public T Deliver(T value, bool ack = true)
        {
            using var packer = Write(value);
            uint id = PeekValueId(packer);
            var decoded = Read(packer);
            if (id != 0)
                lastReceivedId = id;
            if (ack && lastReceivedId != 0)
                sender.ConfirmDeliveryForTests<T>(target, KeyHash, new PackedUInt(lastReceivedId));
            return decoded;
        }

        public void LoseReceiverHistory()
        {
            receiver = new DeltaModule(null, null);
            _history.Clear();
        }
    }

    static void AssertRejected<T>(Wire<T> wire, BitPacker packer, uint expectedMissing)
    {
        Assert.That(Wire<T>.PeekBaseline(packer), Is.EqualTo(expectedMissing),
            "Sender should still reference the baseline the receiver lost");

        LogAssert.Expect(LogType.Error, MissingBaselineLog);
        Assert.Throws<BypassLoggingException>(() => wire.Read(packer),
            "A delta against a baseline the receiver does not have must be dropped, not decoded against default");
    }

    [Test]
    public void PerPlayer_ChangedValue_AgainstMissingBaseline_IsDropped()
    {
        Assert.That(DeltaSharedEncodeInfo<string>.eligible, Is.False);

        var wire = new Wire<string>();
        Assert.That(wire.Deliver("rigidbody state A"), Is.EqualTo("rigidbody state A"));
        Assert.That(wire.Deliver("rigidbody state B"), Is.EqualTo("rigidbody state B"));

        uint acked = wire.lastReceivedId;
        wire.LoseReceiverHistory();

        using var packer = wire.Write("rigidbody state C");
        AssertRejected(wire, packer, acked);
    }

    [Test]
    public void PerPlayer_UnchangedValue_AgainstMissingBaseline_IsDropped()
    {
        var wire = new Wire<string>();
        wire.Deliver("idle");

        uint acked = wire.lastReceivedId;
        wire.LoseReceiverHistory();

        using var packer = wire.Write("idle");
        AssertRejected(wire, packer, acked);
    }

    [Test]
    public void PerPlayer_ReportedMissingBaseline_ResyncsWithFullWrite()
    {
        var wire = new Wire<string>();
        wire.Deliver("first");
        wire.Deliver("second");

        uint lost = wire.lastReceivedId;
        wire.LoseReceiverHistory();

        using (var rejected = wire.Write("third"))
            AssertRejected(wire, rejected, lost);

        wire.sender.ReportMissingBaselineForTests<string>(wire.target, KeyHash, lost);

        using (var full = wire.Write("fourth"))
        {
            Assert.That(Wire<string>.PeekBaseline(full), Is.EqualTo(0u),
                "After the receiver reports a missing baseline the next write must be a full write");
            Assert.That(wire.Read(full), Is.EqualTo("fourth"));
        }

        wire.sender.ConfirmDeliveryForTests<string>(wire.target, KeyHash, new PackedUInt(lost));
        using (var afterStaleAck = wire.Write("fifth"))
        {
            Assert.That(Wire<string>.PeekBaseline(afterStaleAck), Is.EqualTo(0u),
                "A late ack of the reported baseline must not revive it");
            Assert.That(wire.Read(afterStaleAck), Is.EqualTo("fifth"));
        }

        Assert.That(wire.Deliver("sixth"), Is.EqualTo("sixth"));
        Assert.That(wire.Deliver("seventh"), Is.EqualTo("seventh"));
    }

    [Test]
    public void Shared_ChangedValue_AgainstMissingBaseline_IsDropped()
    {
        Assert.That(DeltaSharedEncodeInfo<Vector3>.eligible, Is.True);

        var wire = new Wire<Vector3>();
        wire.Deliver(new Vector3(1, 2, 3));
        wire.Deliver(new Vector3(4, 5, 6));

        uint acked = wire.lastReceivedId;
        wire.LoseReceiverHistory();

        using var packer = wire.Write(new Vector3(7, 8, 9));
        AssertRejected(wire, packer, acked);
    }

    [Test]
    public void Shared_ReportedMissingBaseline_ResyncsWithFullWrite()
    {
        var wire = new Wire<Vector3>();
        wire.Deliver(new Vector3(1, 2, 3));
        wire.Deliver(new Vector3(4, 5, 6));

        uint lost = wire.lastReceivedId;
        wire.LoseReceiverHistory();

        using (var rejected = wire.Write(new Vector3(7, 8, 9)))
            AssertRejected(wire, rejected, lost);

        wire.sender.ReportMissingBaselineForTests<Vector3>(wire.target, KeyHash, lost);

        using (var full = wire.Write(new Vector3(10, 11, 12)))
        {
            Assert.That(Wire<Vector3>.PeekBaseline(full), Is.EqualTo(0u));
            Assert.That(wire.Read(full), Is.EqualTo(new Vector3(10, 11, 12)));
        }

        wire.sender.ConfirmDeliveryForTests<Vector3>(wire.target, KeyHash, new PackedUInt(lost));
        using (var afterStaleAck = wire.Write(new Vector3(13, 14, 15)))
        {
            Assert.That(Wire<Vector3>.PeekBaseline(afterStaleAck), Is.EqualTo(0u));
            Assert.That(wire.Read(afterStaleAck), Is.EqualTo(new Vector3(13, 14, 15)));
        }

        Assert.That(wire.Deliver(new Vector3(16, 17, 18)), Is.EqualTo(new Vector3(16, 17, 18)));
        Assert.That(wire.Deliver(new Vector3(19, 20, 21)), Is.EqualTo(new Vector3(19, 20, 21)));
    }

    [Test]
    public void ReportForSupersededBaseline_IsIgnored()
    {
        var wire = new Wire<string>();
        wire.Deliver("one");
        uint stale = wire.lastReceivedId;
        wire.Deliver("two");
        uint current = wire.lastReceivedId;
        Assert.That(current, Is.GreaterThan(stale));

        wire.sender.ReportMissingBaselineForTests<string>(wire.target, KeyHash, stale);

        using var packer = wire.Write("three");
        Assert.That(Wire<string>.PeekBaseline(packer), Is.EqualTo(current),
            "A late report for an already-superseded baseline must not force a full write");
        Assert.That(wire.Read(packer), Is.EqualTo("three"));
    }
}
