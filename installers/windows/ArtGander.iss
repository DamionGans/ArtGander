; ArtGander Windows installer (Inno Setup 6).
; Build:  iscc /DAppVersion=0.1.0 /DArch=x64 /DSourceDir=..\..\artifacts\publish\win-x64 /DOutputDir=..\..\artifacts\installers ArtGander.iss

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef Arch
  #define Arch "x64"
#endif
#ifndef SourceDir
  #define SourceDir "..\..\artifacts\publish\win-" + Arch
#endif
#ifndef OutputDir
  #define OutputDir "..\..\artifacts\installers"
#endif

[Setup]
AppId={{7F3C2A9E-4B1D-4E8A-9C61-A2D5E0B8F4C3}
AppName=ArtGander
AppVersion={#AppVersion}
AppVerName=ArtGander {#AppVersion}
AppPublisher=ArtGander
AppComments=Artistic Goose On The Loose: manage your art, files, products and sales.
DefaultDirName={autopf}\ArtGander
DefaultGroupName=ArtGander
DisableProgramGroupPage=yes
; Installs for the current user without admin rights. A dialog lets you install for all users instead.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
MinVersion=10.0.17763
OutputDir={#OutputDir}
OutputBaseFilename=ArtGander-Setup-{#AppVersion}-{#Arch}
SetupIconFile=artgander.ico
UninstallDisplayIcon={app}\ArtGander.exe
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
; Code signing hook: define SignTool in CI when a certificate is available.
#ifdef SignTool
SignTool={#SignTool}
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "dutch"; MessagesFile: "compiler:Languages\Dutch.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\ArtGander"; Filename: "{app}\ArtGander.exe"
Name: "{autodesktop}\ArtGander"; Filename: "{app}\ArtGander.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\ArtGander.exe"; Description: "{cm:LaunchProgram,ArtGander}"; Flags: nowait postinstall skipifsilent
