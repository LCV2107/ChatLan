using System.Net;
using MiniChat.Core.Engine;
using MiniChat.Core.Model;
using MiniChat.Core.Protocol;
using MiniChat.Core.Transport;

namespace MiniChat.Tests;

[Collection("engine")]
public class RelationshipTests
{
    [Fact]
    public async Task FullJourney_Discover_Request_Accept_ChatBothWays_CloseAndRecover()
    {
        int disc = TestNet.NextPort();
        await using var ana = await TestPeer.StartAsync("Ana", disc);
        await using var bea = await TestPeer.StartAsync("Bea", disc);

        await Wait.Discovered(ana, bea);
        var seen = await ana.PersonAsync(bea.Fingerprint);
        Assert.Equal("Bea", seen!.DisplayName);
        Assert.Equal("PC-Bea", seen.MachineName);
        Assert.Null(seen.ContactId); // descubierto, aún no es contacto

        Assert.True((await ana.Engine.SendChatRequestAsync(bea.Fingerprint)).Success);
        Assert.Equal(Relation.OutgoingPending, await ana.RelationWithAsync(bea));
        Assert.Equal(Relation.IncomingPending, await bea.RelationWithAsync(ana));
        Assert.Equal(1, bea.NoticeCount(NoticeKind.IncomingRequest));
        // El código de emparejamiento es el mismo en ambos lados
        Assert.Equal(ana.Engine.PairingCodeWith(bea.Fingerprint), bea.Engine.PairingCodeWith(ana.Fingerprint));

        // Antes de aceptar no se puede escribir
        var early = await bea.Engine.SendTextAsync(await bea.ContactIdAsync(ana), "hola?");
        Assert.False(early.Success);

        Assert.True((await bea.Engine.AcceptAsync(await bea.ContactIdAsync(ana))).Success);
        await Wait.Until(async () => await ana.RelationWithAsync(bea) == Relation.Authorized, "Ana autorizada");

        var m1 = await ana.Engine.SendTextAsync(await ana.ContactIdAsync(bea), "Hola Bea 👋");
        Assert.True(m1.Success);
        await Wait.Status(ana, m1.Value!.Id, MessageStatus.Delivered);
        var m2 = await bea.Engine.SendTextAsync(await bea.ContactIdAsync(ana), "¡Hola Ana!\nSegunda línea");
        await Wait.Status(bea, m2.Value!.Id, MessageStatus.Delivered);

        // Cerrar ambos y volver a abrir: historial y autorización se conservan, sin volver a pedir permiso.
        await ana.RestartAsync();
        await bea.RestartAsync();
        Assert.Equal(Relation.Authorized, await ana.RelationWithAsync(bea));
        var anaMsgs = await ana.MessagesWithAsync(bea);
        var beaMsgs = await bea.MessagesWithAsync(ana);
        Assert.Equal(new[] { "Hola Bea 👋", "¡Hola Ana!\nSegunda línea" }, anaMsgs.Select(m => m.Text));
        Assert.Equal(new[] { "Hola Bea 👋", "¡Hola Ana!\nSegunda línea" }, beaMsgs.Select(m => m.Text));

        await Wait.Discovered(ana, bea);
        var m3 = await ana.Engine.SendTextAsync(await ana.ContactIdAsync(bea), "Tras reiniciar");
        await Wait.Status(ana, m3.Value!.Id, MessageStatus.Delivered);
    }

    [Fact]
    public async Task Reject_DoesNotAuthorize_AndRepeatedRequestsAreThrottled()
    {
        int disc = TestNet.NextPort();
        await using var ana = await TestPeer.StartAsync("Ana", disc);
        await using var bea = await TestPeer.StartAsync("Bea", disc);
        await Wait.Discovered(ana, bea);
        await ana.Engine.SendChatRequestAsync(bea.Fingerprint);
        Assert.True((await bea.Engine.RejectAsync(await bea.ContactIdAsync(ana))).Success);
        await Wait.Until(async () => await ana.RelationWithAsync(bea) == Relation.RejectedByPeer, "Ana ve el rechazo");
        Assert.Equal(Relation.RejectedByMe, await bea.RelationWithAsync(ana));

        Assert.False((await ana.Engine.SendTextAsync(await ana.ContactIdAsync(bea), "insisto")).Success);
        Assert.False((await bea.Engine.SendTextAsync(await bea.ContactIdAsync(ana), "x")).Success);

        // Reintento inmediato: el receptor no vuelve a avisar (periodo de espera tras rechazar)
        var again = await ana.Engine.SendChatRequestAsync(bea.Fingerprint);
        Assert.False(again.Success);
        Assert.Equal(1, bea.NoticeCount(NoticeKind.IncomingRequest));
        Assert.Equal(Relation.RejectedByMe, await bea.RelationWithAsync(ana));
    }

    [Fact]
    public async Task ReceiverEnforcesAuthorization_EvenIfSenderBypassesItsUi()
    {
        int disc = TestNet.NextPort();
        await using var ana = await TestPeer.StartAsync("Ana", disc);
        await using var bea = await TestPeer.StartAsync("Bea", disc);
        await Wait.Discovered(ana, bea);

        // Ana (sin autorización) envía un paquete de mensaje directamente, sin pasar por la interfaz.
        var ack = await RawSendAsync(ana, bea, "intruso");
        Assert.Equal(MessageAckStatuses.NotAuthorized, ack.Status);

        // Con solicitud pendiente tampoco
        await ana.Engine.SendChatRequestAsync(bea.Fingerprint);
        Assert.Equal(MessageAckStatuses.NotAuthorized, (await RawSendAsync(ana, bea, "intruso 2")).Status);

        // Bea no guardó nada
        Assert.Empty(await bea.MessagesWithAsync(ana));
        // Conocer la IP y el puerto no basta: solo cuenta la identidad TLS autorizada.
    }

    [Fact]
    public async Task SimultaneousRequests_ResultInMutualAuthorization()
    {
        int disc = TestNet.NextPort();
        await using var ana = await TestPeer.StartAsync("Ana", disc);
        await using var bea = await TestPeer.StartAsync("Bea", disc);
        await Wait.Discovered(ana, bea);
        await Wait.Discovered(bea, ana);
        var t1 = ana.Engine.SendChatRequestAsync(bea.Fingerprint);
        var t2 = bea.Engine.SendChatRequestAsync(ana.Fingerprint);
        var results = await Task.WhenAll(t1, t2);
        Assert.All(results, r => Assert.True(r.Success, r.Message));
        await Wait.Until(async () => await ana.RelationWithAsync(bea) == Relation.Authorized && await bea.RelationWithAsync(ana) == Relation.Authorized,
            "ambos autorizados");
        var m = await bea.Engine.SendTextAsync(await bea.ContactIdAsync(ana), "cruzadas");
        await Wait.Status(bea, m.Value!.Id, MessageStatus.Delivered);
    }

    [Fact]
    public async Task DuplicateRequests_NotifyOnce()
    {
        int disc = TestNet.NextPort();
        await using var ana = await TestPeer.StartAsync("Ana", disc);
        await using var bea = await TestPeer.StartAsync("Bea", disc);
        await Wait.Discovered(ana, bea);
        for (int i = 0; i < 3; i++) Assert.True((await ana.Engine.SendChatRequestAsync(bea.Fingerprint)).Success);
        Assert.Equal(1, bea.NoticeCount(NoticeKind.IncomingRequest));
        var pending = bea.Engine.Db.Read(c => Core.Persistence.RequestsRepo.Pending(c, RequestDirection.Incoming));
        Assert.Single(pending);
    }

    [Fact]
    public async Task AcceptanceLostWhileRequesterOffline_IsRecoveredLater()
    {
        int disc = TestNet.NextPort();
        await using var ana = await TestPeer.StartAsync("Ana", disc);
        await using var bea = await TestPeer.StartAsync("Bea", disc);
        await Wait.Discovered(ana, bea);
        await ana.Engine.SendChatRequestAsync(bea.Fingerprint);

        // Ana cierra la app con la solicitud pendiente. Bea acepta mientras Ana no está.
        await ana.StopAsync();
        var accept = await bea.Engine.AcceptAsync(await bea.ContactIdAsync(ana));
        Assert.True(accept.Success);
        Assert.Contains("cuando esté disponible", accept.Message);
        Assert.Equal(Relation.Authorized, await bea.RelationWithAsync(ana));

        // Ana vuelve: sigue pendiente localmente hasta que la aceptación le llega (reentrega al reaparecer).
        await ana.OpenAsync();
        Assert.Equal(Relation.OutgoingPending, await ana.RelationWithAsync(bea));
        await Wait.Until(async () => await ana.RelationWithAsync(bea) == Relation.Authorized, "Ana recibe la aceptación reentregada", 10000);
    }

    [Fact]
    public async Task CancelBeforeAnswer_LeavesNoAuthorization()
    {
        int disc = TestNet.NextPort();
        await using var ana = await TestPeer.StartAsync("Ana", disc);
        await using var bea = await TestPeer.StartAsync("Bea", disc);
        await Wait.Discovered(ana, bea);
        await ana.Engine.SendChatRequestAsync(bea.Fingerprint);
        Assert.True((await ana.Engine.CancelRequestAsync(await ana.ContactIdAsync(bea))).Success);
        await Wait.Until(async () => await bea.RelationWithAsync(ana) == Relation.None, "Bea ve la cancelación");
        Assert.False((await bea.Engine.AcceptAsync(await bea.ContactIdAsync(ana))).Success);
        Assert.Equal(Relation.None, await ana.RelationWithAsync(bea));
    }

    [Fact]
    public async Task AcceptAfterRequesterCancelledWhileUnreachable_IsRolledBack()
    {
        int disc = TestNet.NextPort();
        await using var ana = await TestPeer.StartAsync("Ana", disc);
        await using var bea = await TestPeer.StartAsync("Bea", disc);
        await Wait.Discovered(ana, bea);
        await ana.Engine.SendChatRequestAsync(bea.Fingerprint);
        // Bea se desconecta; Ana cancela (el aviso no llega)
        await bea.StopAsync();
        await ana.Engine.CancelRequestAsync(await ana.ContactIdAsync(bea));
        // Bea vuelve con la solicitud aún pendiente y la acepta: Ana responde "cancelled" y Bea lo deshace.
        await bea.OpenAsync();
        Assert.Equal(Relation.IncomingPending, await bea.RelationWithAsync(ana));
        await Wait.Discovered(bea, ana);
        await bea.Engine.AcceptAsync(await bea.ContactIdAsync(ana));
        await Wait.Until(async () => await bea.RelationWithAsync(ana) == Relation.None, "Bea deshace la autorización");
        Assert.Equal(Relation.None, await ana.RelationWithAsync(bea));
    }

    [Fact]
    public async Task Block_StopsMessagesAndRequests_KeepsHistory_UnblockDoesNotReauthorize()
    {
        int disc = TestNet.NextPort();
        await using var ana = await TestPeer.StartAsync("Ana", disc);
        await using var bea = await TestPeer.StartAsync("Bea", disc);
        await Wait.Pair(ana, bea);
        var anaToBea = await ana.ContactIdAsync(bea);
        var beaToAna = await bea.ContactIdAsync(ana);
        var m = await ana.Engine.SendTextAsync(anaToBea, "antes del bloqueo");
        await Wait.Status(ana, m.Value!.Id, MessageStatus.Delivered);

        await bea.Engine.BlockAsync(beaToAna);
        var after = await ana.Engine.SendTextAsync(anaToBea, "¿me bloqueaste?");
        await Wait.Status(ana, after.Value!.Id, MessageStatus.Failed);
        Assert.Equal(Relation.RevokedByPeer, await ana.RelationWithAsync(bea));
        // Nueva solicitud: rechazada sin revelar el bloqueo y sin aviso a Bea
        Assert.False((await ana.Engine.SendChatRequestAsync(bea.Fingerprint)).Success);
        Assert.Equal(1, bea.NoticeCount(NoticeKind.NewMessage));
        Assert.Equal(1, bea.NoticeCount(NoticeKind.IncomingRequest)); // solo la del emparejamiento inicial
        // El historial de Bea sigue ahí
        Assert.Single(await bea.MessagesWithAsync(ana));
        // Bea no puede escribir a un contacto bloqueado
        Assert.False((await bea.Engine.SendTextAsync(beaToAna, "x")).Success);

        // Desbloquear no autoriza
        await bea.Engine.UnblockAsync(beaToAna);
        Assert.Equal(Relation.None, await bea.RelationWithAsync(ana));
        Assert.Equal(MessageAckStatuses.NotAuthorized, (await RawSendAsync(ana, bea, "¿y ahora?")).Status);
        // Reintentar los fallidos respeta la revocación
        Assert.False((await ana.Engine.RetryAsync(anaToBea)).Success);
    }

    [Fact]
    public async Task ManualIpConnection_FindsPeer_WithSameIdentityChecks()
    {
        int discA = TestNet.NextPort(), discB = TestNet.NextPort(); // puertos de descubrimiento distintos: no se ven
        await using var ana = await TestPeer.StartAsync("Ana", discA);
        await using var bea = await TestPeer.StartAsync("Bea", discB);
        await Task.Delay(800);
        Assert.Null(await ana.PersonAsync(bea.Fingerprint));

        var probe = await ana.Engine.ConnectManualAsync("127.0.0.1", bea.Options.TcpPort);
        Assert.True(probe.Success, probe.Message);
        Assert.Equal(bea.Fingerprint, probe.Value!.Fingerprint);
        Assert.True((await ana.Engine.SendChatRequestAsync(bea.Fingerprint)).Success);
        await bea.Engine.AcceptAsync(await bea.ContactIdAsync(ana));
        await Wait.Until(async () => await ana.RelationWithAsync(bea) == Relation.Authorized, "autorizada por IP manual");

        var bad = await ana.Engine.ConnectManualAsync("10.255.255.1");
        Assert.False(bad.Success);
        Assert.Contains("no pertenece", bad.Message);
        Assert.False((await ana.Engine.ConnectManualAsync("no-es-ip")).Success);
        var refused = await ana.Engine.ConnectManualAsync("127.0.0.1", TestNet.NextPort());
        Assert.False(refused.Success);
    }

    [Fact]
    public async Task EndpointChange_SameIdentity_KeepsSameContact()
    {
        int disc = TestNet.NextPort();
        await using var ana = await TestPeer.StartAsync("Ana", disc);
        await using var bea = await TestPeer.StartAsync("Bea", disc);
        await Wait.Pair(ana, bea);
        var idBefore = await ana.ContactIdAsync(bea);

        // Bea vuelve con otro puerto (equivalente a otra IP): misma identidad, mismo contacto.
        await bea.RestartAsync(newTcpPort: TestNet.NextPort());
        await Wait.Until(async () => (await ana.PersonAsync(bea.Fingerprint))?.Endpoint?.Port == bea.Options.TcpPort, "nuevo puerto anunciado");
        Assert.Equal(idBefore, await ana.ContactIdAsync(bea));
        Assert.Single((await ana.Engine.GetPeopleAsync()).Where(p => p.DisplayName == "Bea"));
        var m = await ana.Engine.SendTextAsync(idBefore, "¿sigues ahí?");
        await Wait.Status(ana, m.Value!.Id, MessageStatus.Delivered);
    }

    [Fact]
    public async Task IdentityChange_IsDetected_AndRequiresNewPairing()
    {
        int disc = TestNet.NextPort();
        await using var ana = await TestPeer.StartAsync("Ana", disc);
        await using var bea = await TestPeer.StartAsync("Bea", disc);
        await Wait.Pair(ana, bea);
        var oldFp = bea.Fingerprint;
        var anaToBea = await ana.ContactIdAsync(bea);

        // Bea pierde su clave (reinstalación) y vuelve en la misma dirección.
        await bea.StopAsync();
        bea.LoseIdentityKey();
        await bea.OpenAsync();
        Assert.True(bea.Engine.IdentityRegenerated);
        Assert.NotEqual(oldFp, bea.Fingerprint);

        // Ana ve una identidad nueva marcada como posible cambio de identidad del contacto anterior.
        await Wait.Discovered(ana, bea);
        var newcomer = await ana.PersonAsync(bea.Fingerprint);
        Assert.Null(newcomer!.ContactId);
        Assert.Equal(anaToBea, newcomer.PossibleIdentityChangeOfContactId);

        // El contacto antiguo no queda autorizado para la nueva clave: enviar falla con explicación.
        var m = await ana.Engine.SendTextAsync(anaToBea, "¿eres tú?");
        await Wait.Status(ana, m.Value!.Id, MessageStatus.Failed);
        var failed = await ana.Engine.GetMessageAsync(m.Value.Id);
        Assert.Contains("identidad distinta", failed!.Error);
        // Y la nueva identidad de Bea no puede escribir a Ana sin emparejarse
        Assert.Equal(MessageAckStatuses.NotAuthorized, (await RawSendAsync(bea, ana, "soy Bea")).Status);
    }

    /// <summary>Envía un paquete de mensaje "a mano" (como haría un cliente modificado).</summary>
    internal static async Task<MessageAckPacket> RawSendAsync(TestPeer from, TestPeer to, string text, string? uid = null)
    {
        await using var client = await PeerClient.ConnectAsync(new IPEndPoint(IPAddress.Loopback, to.Options.TcpPort), to.Fingerprint,
            from.Engine.Identity.Certificate, new LocalHello(Guid.NewGuid().ToString("N"), from.Name, "PC", from.Options.TcpPort, "test"),
            from.Engine.Policy, from.Options, CancellationToken.None);
        var reply = await client.RequestAsync(new MessagePacket { MessageId = uid ?? Guid.NewGuid().ToString("N"), Text = text, Sequence = 1 }, default, CancellationToken.None);
        return Assert.IsType<MessageAckPacket>(reply.Packet);
    }
}

[CollectionDefinition("engine", DisableParallelization = true)]
public class EngineCollection;
