using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using PurrNet;
using PurrNet.Transports;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

public class CompositeTransportChildStopTests
{
    static readonly FieldInfo TransportsField =
        typeof(CompositeTransport).GetField("_transports", BindingFlags.Instance | BindingFlags.NonPublic);

    readonly List<Object> _created = new();
    NetworkManager _manager;

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        if (_manager)
        {
            _manager.StopClient();
            _manager.StopServer();
            for (int i = 0; i < 10; i++)
                yield return null;
        }

        for (int i = _created.Count - 1; i >= 0; i--)
        {
            if (_created[i])
                Object.DestroyImmediate(_created[i]);
        }

        _created.Clear();
        _manager = null;
    }

    IEnumerator WaitUntil(System.Func<bool> condition, float timeout = 10f)
    {
        var deadline = Time.realtimeSinceStartup + timeout;
        while (!condition() && Time.realtimeSinceStartup < deadline)
            yield return null;
    }

    [UnityTest]
    public IEnumerator ChildStopsListening_DisconnectsItsConnections()
    {
        var go = new GameObject("CompositeNetworkManager");
        go.SetActive(false);
        _created.Add(go);

        var udpA = go.AddComponent<UDPTransport>();
        udpA.serverPort = 47910;
        var udpB = go.AddComponent<UDPTransport>();
        udpB.serverPort = 47911;
        var composite = go.AddComponent<CompositeTransport>();
        TransportsField.SetValue(composite, new GenericTransport[] { udpA, udpB });

        _manager = go.AddComponent<NetworkManager>();
        _manager.startServerFlags = StartFlags.None;
        _manager.startClientFlags = StartFlags.None;

        var rules = ScriptableObject.CreateInstance<NetworkRules>();
        _created.Add(rules);
        _manager.SetNetworkRules(rules);

        var provider = ScriptableObject.CreateInstance<NetworkPrefabs>();
        provider.autoGenerate = false;
        _created.Add(provider);
        _manager.SetPrefabProvider(provider);

        _manager.transport = composite;
        go.SetActive(true);

        composite.SetClientTransport(udpB);
        _manager.StartHost();
        yield return WaitUntil(() => _manager.isServer && _manager.isClient && _manager.playerCount == 1);

        Assert.That(_manager.playerCount, Is.EqualTo(1), "host client did not join through the second child");
        Assert.That(composite.connections.Count, Is.EqualTo(1));

        udpB.transport.StopListening();
        yield return WaitUntil(() => composite.connections.Count == 0 && _manager.playerCount == 0, 3f);

        Assert.That(composite.connections.Count, Is.EqualTo(0), "composite kept the stopped child's connection");
        Assert.That(_manager.playerCount, Is.EqualTo(0), "server kept the player that joined through the stopped child");
    }
}
