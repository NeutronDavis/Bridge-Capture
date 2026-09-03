; =====================================================================
; Bridge Capture — Inno Setup Script
; Generates a single-file installer (NDDC-HRMS-Capture-Setup.exe) that installs:
;   1. Self-contained .NET 10 Bridge-Capture application
;   2. ZKTeco Biokey OCX SDK files + regsvr32 registration
;   3. SSL Certificate installation into Trusted Root Certification Authorities
;   4. Desktop System Tray Auto-Startup + Desktop Shortcut
;   5. Custom NDDC Branding & Application Icon
; =====================================================================

[Setup]
AppName=NDDC-HRMS-Capture
AppVersion=1.0
AppPublisher=SouthBridge
AppPublisherURL=https://southbridge.com
DefaultDirName={commonpf32}\NDDC-HRMS-Capture
DefaultGroupName=NDDC-HRMS-Capture
OutputBaseFilename=NDDC-HRMS-Capture-Setup
SetupIconFile=app.ico
UninstallDisplayIcon={app}\app.ico
Compression=lzma2/max
SolidCompression=yes
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
; 1. Application Files (from self-contained publish directory)
Source: "publish\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion
; Application Icon
Source: "app.ico"; DestDir: "{app}"; Flags: ignoreversion

; 2. ZKTeco SDK OCX & DLL Dependencies
Source: "C:\Program Files (x86)\FPSensor\Biokey\biokey.ocx";        DestDir: "{app}"; Flags: ignoreversion
Source: "C:\Program Files (x86)\FPSensor\Biokey\*.dll";             DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "C:\Program Files (x86)\FPSensor\Biokey\ZKFPSensors\*.dll"; DestDir: "{app}\ZKFPSensors"; Flags: ignoreversion skipifsourcedoesntexist

[Icons]
; Start Menu shortcut
Name: "{group}\NDDC-HRMS-Capture"; Filename: "{app}\Bridge-capture.exe"; IconFilename: "{app}\app.ico"
; Desktop shortcut (created during installation)
Name: "{autodesktop}\NDDC-HRMS-Capture"; Filename: "{app}\Bridge-capture.exe"; Tasks: desktopicon; IconFilename: "{app}\app.ico"
; Windows Startup shortcut — auto-starts on login with System Tray icon visible!
Name: "{userstartup}\NDDC-HRMS-Capture"; Filename: "{app}\Bridge-capture.exe"; IconFilename: "{app}\app.ico"

[Run]
; A. Register the 32-bit ZKTeco Biokey OCX
Filename: "{syswow64}\regsvr32.exe"; Parameters: "/s ""{app}\biokey.ocx"""; StatusMsg: "Registering ZKTeco Fingerprint SDK..."; Flags: runhidden

; B. Import localhost.pfx into the target machine's Trusted Root Certification Authorities
;    This makes wss://localhost:5050 trusted in Chrome, Edge, and Windows without user prompts.
Filename: "certutil.exe"; Parameters: "-f -p ""BridgeCapture@Secure2024"" -importpfx Root ""{app}\localhost.pfx"""; StatusMsg: "Installing SSL Security Certificate..."; Flags: runhidden

; C. Launch Bridge Capture immediately in the user's Desktop session (System Tray icon will appear!)
Filename: "{app}\Bridge-capture.exe"; Description: "Launch NDDC-HRMS-Capture System Tray App"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Clean up registration on uninstall
Filename: "{syswow64}\regsvr32.exe"; Parameters: "/s /u ""{app}\biokey.ocx"""; Flags: runhidden
