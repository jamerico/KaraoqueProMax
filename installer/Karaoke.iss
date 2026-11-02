; Inno Setup script for Karaokê — builds installer\KaraokeSetup.exe from ..\dist (self-contained .NET + VLC + ffmpeg)
#define AppName "Karaokê"
#define AppVersion "1.1"

[Setup]
AppId={{6B2E8F7A-3C1D-4E59-9A0B-KARAOKEJEAN01}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Jean
DefaultDirName={autopf}\Karaoke
DefaultGroupName={#AppName}
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\Karaoke.exe
OutputDir=.
OutputBaseFilename=KaraokeSetup
Compression=lzma2/ultra64
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
WizardStyle=modern
DisableProgramGroupPage=yes

[Languages]
Name: "pt"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "Criar atalho na área de trabalho"; GroupDescription: "Atalhos:"

[Files]
Source: "..\dist\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\Karaoke.exe"
Name: "{group}\{#AppName} (gravar para calibrar)"; Filename: "{app}\Karaoke.exe"; Parameters: "--record ""{userdocs}\Karaoke gravacoes"""
Name: "{group}\Desinstalar {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\Karaoke.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Karaoke.exe"; Parameters: "--warmup"; StatusMsg: "Preparando o player de vídeo (pode levar cerca de 1 minuto)…"; Flags: runhidden waituntilterminated
Filename: "{app}\Karaoke.exe"; Description: "Abrir o Karaokê agora"; Flags: nowait postinstall skipifsilent
