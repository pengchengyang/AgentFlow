// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="CFGSTcpProtocolBroadcasts.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentFlow.Shared.RTS.CFGS.Broadcasts;

/// <summary>Shared topics used by external product GUIs to observe CFG/CFGS TCP sessions.</summary>
public static class CFGSTcpProtocolBroadcasts
{
    /// <summary>CFGS server connection state. Payload is a keyed string Sample dictionary.</summary>
    public const string CFGSServerConnectionStatus =
        "CFGS.Tcp.Server.Connection.Status.1FFD93B9-9244-48A4-8E2B-7CB3057A3314";

    /// <summary>CFG client connection state. Payload is a keyed string Sample dictionary.</summary>
    public const string CFGTcpClientConnectionStatus =
        "CFG.Tcp.Client.Connection.Status.DC4BB9BC-CEA6-41A5-A337-A73DE9964599";

    /// <summary>
    /// Per-slot CFG test launches. Node.CFGSHandler publishes one keyed string Sample
    /// dictionary per STATUS=TEST record when it sends a BUSINESS launch batch to a CFG;
    /// GUI consumers mark one slot per message as testing. Uses CFGSTestResultKeys.
    /// </summary>
    public const string CFGSTestLaunch =
        "CFGS.Test.Launch.2AF2F7F1-86BA-4BA0-BDB5-BA26A9115690";

    /// <summary>
    /// Per-slot CFG test results. Node.CFGSHandler publishes one keyed string Sample
    /// dictionary per BUSINESS MESSAGES record received from a CFG; GUI consumers update
    /// one slot per message. STATUS is the decimal RET code, where 0 is a pass and any
    /// non-zero value is a failure.
    /// </summary>
    public const string CFGSTestResult =
        "CFGS.Test.Result.749B4404-563A-41F7-AB8B-7E360C01CFF6";
}

/// <summary>Standard keys used in connection-status broadcast dictionaries.</summary>
public static class CFGSTcpProtocolStatusKeys
{
    public const string GUID = "GUID";
    public const string ROLE = "ROLE";
    public const string STATE = "STATE";
    public const string ENDPOINT = "ENDPOINT";
    public const string LAST_SEEN_UTC = "LAST_SEEN_UTC";
    public const string PENDING_SEND_COUNT = "PENDING_SEND_COUNT";
}

/// <summary>Connection states published by CFGS server and CFG client nodes.</summary>
public static class CFGSTcpProtocolConnectionStates
{
    public const string Disconnected = "DISCONNECTED";
    public const string Connecting = "CONNECTING";
    public const string Authenticating = "AUTHENTICATING";
    public const string Authenticated = "AUTHENTICATED";
    public const string AuthenticatedClient = "AUTHENTICATED_CLIENT";
    public const string Failed = "FAILED";
}

/// <summary>Standard keys used in CFGS test-launch and test-result broadcast dictionaries.</summary>
public static class CFGSTestResultKeys
{
    public const string SLOT = "SLOT";
    public const string FSN = "FSN";
    public const string IMEI = "IMEI";
    public const string STATUS = "STATUS";
    public const string GUID = "GUID";
    public const string LOCATION = "LOCATION";
    public const string SEQ = "SEQ";
    public const string TIMESTAMP = "TIMESTAMP";
}
