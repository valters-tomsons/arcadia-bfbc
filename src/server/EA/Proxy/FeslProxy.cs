using System.Net.Sockets;
using System.Text.RegularExpressions;
using Arcadia.EA.Constants;
using Arcadia.Tls;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;

namespace Arcadia.EA.Proxy;

public partial class FeslProxy
{
    private readonly ILogger<FeslProxy> _logger;
    
    private readonly FeslSettings _feslSettings;
    private readonly ProxySettings _proxySettings;

    private TlsServerProtocol? _arcadiaProtocol;
    private TlsClientProtocol? _upstreamProtocol;
    private BcTlsCrypto? _crypto;

    private string? _personaName;
    private string? _originalPartition;

    public FeslProxy(ILogger<FeslProxy> logger, IOptions<FeslSettings> feslSettings, IOptions<ProxySettings> proxySettings)
    {
        _logger = logger;
        _feslSettings = feslSettings.Value;
        _proxySettings = proxySettings.Value;
    }

    public async Task StartProxy(TlsServerProtocol arcadiaProtocol, BcTlsCrypto crypto)
    {
        _arcadiaProtocol = arcadiaProtocol;
        _crypto = crypto;

        InitializeUpstreamClient();
        await StartProxying();
    }

    private void InitializeUpstreamClient()
    {
        _logger.LogInformation("Connecting to upstream {ServerAddress}:{ServerPort}", _feslSettings.ServerAddress, _feslSettings.ServerPort);

        var upstreamTcpClient = new TcpClient(_feslSettings.ServerAddress, _feslSettings.ServerPort);
        var upstreamTcpStream = upstreamTcpClient.GetStream();
        _upstreamProtocol = new TlsClientProtocol(upstreamTcpStream);

        var proxyTlsAuth = new ProxyTlsAuthentication(_logger);
        var upstreamClient = new Ssl3TlsClient(_crypto!, proxyTlsAuth);

        try
        {
            _upstreamProtocol.Connect(upstreamClient);
            _logger.LogDebug("SSL Handshake with upstream successful!");
        }
        catch(Exception e)
        {
            _logger.LogError("Failed to connect to upstream: {Message}", e.Message);
            throw new Exception($"Failed to connect to upstream {_feslSettings.ServerAddress}:{_feslSettings.ServerPort}");
        }
    }

    private async Task StartProxying()
    {
        var clientToFeslTask = Task.Run(() =>
        {
            try
            {
                while (_arcadiaProtocol!.IsConnected)
                {
                    ProxyApplicationData(_arcadiaProtocol, _upstreamProtocol!);
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Failed to proxy data from client: {Message}", e.Message);
            }

            return Task.CompletedTask;
        });

        var feslToClientTask = Task.Run(() =>
        {
            try
            {
                while (_arcadiaProtocol!.IsConnected)
                {
                    ProxyApplicationData(_upstreamProtocol!, _arcadiaProtocol);
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Failed to proxy data from upstream: {Message}", e.Message);
            }
            return Task.CompletedTask;

        });

        await Task.WhenAny(clientToFeslTask, feslToClientTask);
        _logger.LogInformation("Proxy connection closed");
    }

    private async void ProxyApplicationData(TlsProtocol source, TlsProtocol destination)
    {
        var readBuffer = new byte[8096];
        int? read = 0;

        while (source.IsConnected)
        {
            try
            {
                read = source.ReadApplicationData(readBuffer, 0, readBuffer.Length);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Failed to read proxy data");
                return;
            }

            if (!(read > 0))
            {
                continue;
            }

            var incomingPacket = AnalyzeFeslPacket(readBuffer.AsSpan(0, read.Value).ToArray());
            LogPacket("Proxying", incomingPacket);

            if (incomingPacket != null)
            {
                var (packet, numOverridesApplied, respond) = PotentiallyApplyOverridesToPacket(incomingPacket.Value);
                if (numOverridesApplied > 0)
                {
                    LogPacket($"Packet with {numOverridesApplied} override(s) applied", packet);
                    
                    var newBuffer = await packet.Serialize();

                    read = newBuffer.Length;
                    Array.Copy(newBuffer, readBuffer, read.Value);
                }

                if (respond)
                {
                    LogPacket("Responding with packet", packet);
                    try
                    {
                        source.WriteApplicationData(readBuffer, 0, read.Value);
                        return; // We want to consume the response, so skip sending it to the client
                    }
                    catch (Exception e)
                    {
                        _logger.LogError(e, "Failed to write proxy data");
                        return;
                    }
                }
            }

            try
            {
                destination.WriteApplicationData(readBuffer, 0, read.Value);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Failed to write proxy data");
                return;
            }
        }
    }

    private (Packet, int, bool) PotentiallyApplyOverridesToPacket(Packet originalPacket)
    {
        var packet = originalPacket.Clone();
        var type = packet.Type;
        var transmissionType = packet.TransmissionType;
        var txn = packet["TXN"];
        var numOverridesApplied = 0;
        
        var enableTheaterOverride = !string.IsNullOrWhiteSpace(_proxySettings.ProxyOverrideTheaterIp) ||
                                    _proxySettings.ProxyOverrideTheaterPort != 0;
        if (enableTheaterOverride &&
            type == "fsys" &&
            transmissionType == FeslTransmissionType.SinglePacketResponse &&
            txn == "Hello")
        {
            _logger.LogInformation("Overriding server theater details...");

            if (!string.IsNullOrWhiteSpace(_proxySettings.ProxyOverrideTheaterIp))
            {
                packet["theaterIp"] = _proxySettings.ProxyOverrideTheaterIp;
                numOverridesApplied++;
            }

            if (_proxySettings.ProxyOverrideTheaterPort != 0)
            {
                packet["theaterPort"] = _proxySettings.ProxyOverrideTheaterPort.ToString();
                numOverridesApplied++;
            }
        }

        var enablePlatformOverride = _proxySettings.ProxyOverrideAccountIsXbox;
        if (enablePlatformOverride && 
            type == "fsys" &&
            transmissionType == FeslTransmissionType.SinglePacketRequest &&
            txn == "Hello")
        {
            var clientString = packet["clientString"];
            if (!string.IsNullOrWhiteSpace(clientString))
            {
                _logger.LogInformation("Overriding client string...");
                
                packet["clientString"] = clientString.Replace("ps3", "360");
                numOverridesApplied++;
            }
        }
        
        if (enablePlatformOverride &&
            type == "pnow" &&
            transmissionType == FeslTransmissionType.SinglePacketRequest &&
            txn == "Start")
        {
            var partition = packet["partition.partition"];
            if (!string.IsNullOrWhiteSpace(partition))
            {
                _logger.LogInformation("Overriding playnow request partition...");

                _originalPartition = partition;

                packet["partition.partition"] = PartitionRegex().Replace(partition, "/ps3/$1");
                numOverridesApplied++;
            }
        }
        
        if (enablePlatformOverride &&
            type == "pnow" &&
            transmissionType == FeslTransmissionType.SinglePacketResponse &&
            (txn == "Start" || txn == "Status"))
        {
            if (!string.IsNullOrWhiteSpace(_originalPartition))
            {
                _logger.LogInformation("Overriding playnow response partition...");

                packet["id.partition"] = _originalPartition;
                numOverridesApplied++;
            }
        }

        var enablePlayNowGameOverride =
            _proxySettings.ProxyOverridePlayNowGameGid != 0 && _proxySettings.ProxyOverridePlayNowGameLid != 0;
        if (enablePlayNowGameOverride &&
            type == "pnow" &&
            transmissionType == FeslTransmissionType.SinglePacketResponse &&
            txn == "Status")
        {
            _logger.LogInformation("Overriding play now response server...");

            var overridePacketData = new Dictionary<string, object>
            {
                { "TXN", "Status" },
                { "id.id", packet["id.id"] },
                { "id.partition", packet["id.partition"] },
                { "sessionState", "COMPLETE" },
                { "props.{}", 2 },
                { "props.{resultType}", "JOIN" },
                { "props.{games}.[]", 1 },
                { "props.{games}.0.fit", 3349 },
                { "props.{games}.0.gid", _proxySettings.ProxyOverridePlayNowGameGid },
                { "props.{games}.0.lid", _proxySettings.ProxyOverridePlayNowGameLid }
            };

            packet = new Packet("pnow", FeslTransmissionType.SinglePacketResponse, packet.Id, overridePacketData);
            numOverridesApplied++;
        }

        if (type == "acct" &&
            transmissionType == FeslTransmissionType.SinglePacketRequest &&
            txn == "NuPS3Login")
        {
            var clientTicket = packet["ticket"];
            if (!string.IsNullOrWhiteSpace(clientTicket) && _proxySettings.DumpClientTicket)
            {
                _logger.LogCritical(clientTicket);
                throw new Exception("Ticket dumped, exiting...");
            }

            if (!string.IsNullOrWhiteSpace(clientTicket) &&
                !string.IsNullOrWhiteSpace(_proxySettings.ProxyOverrideClientTicket))
            {
                _logger.LogInformation("Overriding client ticket...");

                packet["ticket"] = _proxySettings.ProxyOverrideClientTicket;
                numOverridesApplied++;
            }
        }

        var enableLoginOverride = !string.IsNullOrWhiteSpace(_proxySettings.ProxyOverrideAccountEmail) &&
                                  !string.IsNullOrWhiteSpace(_proxySettings.ProxyOverrideAccountPassword);
        if (enableLoginOverride &&
            type == "acct" &&
            transmissionType == FeslTransmissionType.SinglePacketRequest &&
            txn == "NuPS3Login")
        {
            var macAddr = !string.IsNullOrWhiteSpace(_proxySettings.ProxyOverrideClientMacAddr)
                ? _proxySettings.ProxyOverrideClientMacAddr
                : packet["macAddr"];

            _logger.LogInformation("Overriding client login request...");

            var overridePacketData = new Dictionary<string, object>
            {
                { "TXN", "NuLogin" },
                { "returnEncryptedInfo", 0 },
                { "nuid", _proxySettings.ProxyOverrideAccountEmail },
                { "password", _proxySettings.ProxyOverrideAccountPassword },
                { "macAddr", macAddr },
            };
            
            // Add tosVersion param if present in the original packet, so this login can update the TOS version
            var tosVersion = packet["tosVersion"];
            if (!string.IsNullOrWhiteSpace(tosVersion))
            {
                overridePacketData["tosVersion"] = tosVersion;
            }

            packet = new Packet("acct", FeslTransmissionType.SinglePacketRequest, packet.Id, overridePacketData);
            numOverridesApplied++;
        }

        if (enableLoginOverride &&
            type == "acct" &&
            transmissionType == FeslTransmissionType.SinglePacketResponse &&
            txn == "NuLogin")
        {
            // Make sure response contains an lkey, indicating successful login
            var lkey = packet["lkey"];
            if (!string.IsNullOrWhiteSpace(lkey))
            {
                _logger.LogInformation("Consuming server login response...");

                var overridePacketData = new Dictionary<string, object>
                {
                    { "TXN", "NuGetPersonas" },
                    { "namespace", "" },
                };

                packet = new Packet("acct", FeslTransmissionType.SinglePacketRequest, packet.Id, overridePacketData);
                numOverridesApplied++;
                
                return (packet, numOverridesApplied, true); // Directly respond to sender with override ticket
            }
        }

        if (enableLoginOverride &&
            type == "acct" &&
            transmissionType == FeslTransmissionType.SinglePacketResponse &&
            txn == "NuGetPersonas")
        {
            // Ensure response contains a persona we can use
            var personaName = packet["personas.0"];
            if (!string.IsNullOrWhiteSpace(personaName))
            {
                _logger.LogInformation("Consuming server get personas response...");
                
                _personaName = personaName;

                var overridePacketData = new Dictionary<string, object>
                {
                    { "TXN", "NuLoginPersona" },
                    { "name", _personaName },
                };
                
                packet = new Packet("acct", FeslTransmissionType.SinglePacketRequest, packet.Id, overridePacketData);
                numOverridesApplied++;
                
                return (packet, numOverridesApplied, true); // Directly respond to sender with override ticket
            }
        }

        if (enableLoginOverride &&
            type == "acct" &&
            transmissionType == FeslTransmissionType.SinglePacketResponse &&
            txn == "NuLoginPersona")
        {
            // Ensure we have both an lkey in the response and a persona name we can use
            var lkey = packet["lkey"];
            if (!string.IsNullOrWhiteSpace(lkey) && !string.IsNullOrWhiteSpace(_personaName))
            {
                _logger.LogInformation("Overriding server profile details...");

                var overridePacketData = new Dictionary<string, object>
                {
                    { "TXN", "PS3Login" },
                    { "userId", packet["userId"] },
                    { "personaName", _personaName },
                    { "lkey", lkey }
                };

                packet = new Packet("acct", FeslTransmissionType.SinglePacketResponse, packet.Id, overridePacketData);
                numOverridesApplied++;
            }
        }
        
        return (packet, numOverridesApplied, false);
    }

    private void LogPacket(string msg, Packet? packet)
    {
        var dataStringMod = packet?.DataDict.Select(x => $"{x.Key}={x.Value}").Aggregate((x, y) => $"{x}; {y}");
        _logger.LogTrace("{msg} id={Id} len={Length} {Type}, data: {dataString}", msg, packet?.Id, packet?.Length, packet?.Type, dataStringMod);
    }

    private static Packet? AnalyzeFeslPacket(byte[] buffer)
    {
        var packet = new Packet(buffer);
        if (!packet.DataDict.TryGetValue("TXN", out var txnObj) || txnObj == null)
        {
            return null;
        }

        var txn = txnObj as string;
        if (string.IsNullOrWhiteSpace(txn)) return null;

        return packet;
    }

    [GeneratedRegex("^/.*?/(.*)$")]
    private static partial Regex PartitionRegex();
}

public class ProxyTlsAuthentication : TlsAuthentication
{
    private readonly ILogger _logger;

    public ProxyTlsAuthentication(ILogger logger)
    {
        _logger = logger;
    }

    public TlsCredentials GetClientCredentials(CertificateRequest certificateRequest)
    {
        throw new NotImplementedException();
    }

    public void NotifyServerCertificate(TlsServerCertificate serverCertificate)
    {
        _logger.LogDebug("Ignoring server certificate...");
    }
}