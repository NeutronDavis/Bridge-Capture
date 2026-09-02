; =====================================================================
; Bridge Capture — Inno Setup Script
; Generates a single-file installer (BridgeCaptureSetup.exe) that installs:
;   1. Self-contained .NET 10 Bridge-Capture application
;   2. ZKTeco Biokey OCX SDK files + regsvr32 registration
;   3. SSL Certificate installation into Trusted Root Certification Authorities
;   4. Windows Service registration (BridgeCaptureService, Auto-start)
; =====================================================================

[Setup]
AppName=Bridge Capture
AppVersion=1.0
AppPublisher=Your Company
DefaultDirName={commonpf32}\BridgeCapture
DefaultGroupName=Bridge Capture
OutputBaseFilename=BridgeCaptureSetup
Compression=lzma2/max
SolidCompression=yes
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=

[Files]
; 1. Application Files (from self-contained publish directory)
Source: "publish\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

; 2. ZKTeco SDK OCX & DLL Dependencies
Source: "C:\Program Files (x86)\FPSensor\Biokey\biokey.ocx";        DestDir: "{app}"; Flags: ignoreversion
Source: "C:\Program Files (x86)\FPSensor\Biokey\*.dll";             DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "C:\Program Files (x86)\FPSensor\Biokey\ZKFPSensors\*.dll"; DestDir: "{app}\ZKFPSensors"; Flags: ignoreversion skipifsourcedoesntexist

[Run]
; A. Register the 32-bit ZKTeco Biokey OCX
Filename: "{syswow64}\regsvr32.exe"; Parameters: "/s ""{app}\biokey.ocx"""; StatusMsg: "Registering ZKTeco Fingerprint SDK..."; Flags: runhidden

; B. Import localhost.pfx into the target machine's Trusted Root Certification Authorities
;    This makes wss://localhost:5050 trusted in Chrome, Edge, and Windows without user prompts.
Filename: "certutil.exe"; Parameters: "-f -p ""BridgeCapture@Secure2024"" -importpfx Root ""{app}\localhost.pfx"""; StatusMsg: "Installing SSL Security Certificate..."; Flags: runhidden

; C. Register as an Automatic Windows Service
Filename: "{sys}\sc.exe"; Parameters: "create BridgeCaptureService binPath=""{app}\Bridge-capture.exe"" start=auto DisplayName=""Bridge Capture Fingerprint Service"""; StatusMsg: "Configuring Windows Service..."; Flags: runhidden

; D. Start the service immediately
Filename: "{sys}\sc.exe"; Parameters: "start BridgeCaptureService"; StatusMsg: "Starting Bridge Capture Service..."; Flags: runhidden

[UninstallRun]
; Clean up service and registration on uninstall
Filename: "{sys}\sc.exe"; Parameters: "stop BridgeCaptureService"; Flags: runhidden
Filename: "{sys}\sc.exe"; Parameters: "delete BridgeCaptureService"; Flags: runhidden
Filename: "{syswow64}\regsvr32.exe"; Parameters: "/s /u ""{app}\biokey.ocx"""; Flags: runhidden

[Icons]
Name: "{group}\Bridge Capture"; Filename: "{app}\Bridge-capture.exe"
