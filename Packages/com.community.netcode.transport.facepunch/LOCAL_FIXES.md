This embedded copy preserves Facepunch transport 2.0.0 and its existing script
GUIDs and native plugin import settings. The session coordinator prepares sockets
before NGO initializes a session, reports preparation errors through StartupError,
and publishes the selected virtual relay port in Steam lobby data. Client sockets
connect to the published port; old lobbies without that key use port 0.

Server preparation retries an invalid listen handle on another virtual port.
Shutdown independently closes and clears both managers and resets the relay
initialization flag. It shuts down the global Steam API only if the transport
initialized it itself, preserving an API owned by NetworkSessionCoordinator.
The coordinator releases its owned Steam API when its lifetime root is destroyed.
