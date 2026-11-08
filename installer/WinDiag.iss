; WinDiag.iss - script do instalador (Inno Setup 6.x)
;
; Compilar (o publish.ps1 faz isto automaticamente):
;   ISCC.exe /DMyAppVersion=1.0.0 /DSourceExe=C:\caminho\WinDiag-Portable-1.0.0.exe /O..\dist installer\WinDiag.iss
;
; Parametros:
;   /DMyAppVersion  versao do app (lida de app\Directory.Build.props pelo publish.ps1)
;   /DSourceExe     WinDiag.exe self-contained publicado (opcional; padrao: dist\WinDiag-Portable-{versao}.exe)
;
; O instalador nao registra servicos nem drivers. Copia apenas o programa e atalhos.
; Este arquivo esta salvo em UTF-8 com BOM (necessario para os acentos em pt-BR).

#ifndef MyAppVersion
  #error Informe a versao: ISCC.exe /DMyAppVersion=1.0.0 installer\WinDiag.iss
#endif
#ifndef SourceExe
  #define SourceExe "..\dist\WinDiag-Portable-" + MyAppVersion + ".exe"
#endif

; Repositorio oficial do projeto
#define MyAppURL "https://github.com/dougllascda/WinDiag"
#define MyAppName "WinDiag"
#define MyAppPublisher "dougllascda"
#define MyAppExeName "WinDiag.exe"

[Setup]
; GUID fixo e exclusivo deste app (nao altere, para que atualizacoes substituam a instalacao anterior).
AppId={{6B0D5E1C-3F2A-4C7E-9A41-5D8E2B7C90F3}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
VersionInfoVersion={#MyAppVersion}
DefaultDirName={autopf}\WinDiag
DefaultGroupName=WinDiag
DisableProgramGroupPage=yes
; O app ja pede elevacao (UAC) ao abrir; o instalador tambem roda como administrador.
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupIconFile=..\app\WinDiag.App\Assets\WinDiag.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
LicenseFile=..\LICENSE
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
OutputBaseFilename=WinDiag-Setup-{#MyAppVersion}
; Saida: dist\ por padrao (o publish.ps1 tambem passa /O).
OutputDir=..\dist

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
brazilianportuguese.AskRemoveUserData=Deseja também remover as preferências e o histórico do WinDiag?%n%n%1%n%2%n%nOs snapshots de otimizações em %3 NÃO serão removidos.
english.AskRemoveUserData=Do you also want to remove WinDiag preferences and history?%n%n%1%n%2%n%nOptimization snapshots in %3 will NOT be removed.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; DestName: "{#MyAppExeName}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\WinDiag"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,WinDiag}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\WinDiag"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,WinDiag}"; Flags: nowait postinstall skipifsilent

; A desinstalacao remove apenas a pasta do programa. Preferencias (%AppData%\WinDiag) e historico
; (%LocalAppData%\WinDiag) ficam, a menos que o usuario aceite removê-los na pergunta abaixo.
; Snapshots em %ProgramData%\WinDiagMaint\snapshots nunca sao removidos (permitem desfazer otimizacoes).
; LIMITACAO: {userappdata} e {localappdata} apontam para o perfil de quem executa o desinstalador. Se outro
; administrador (ou uma conta elevada diferente do usuario que usou o app) desinstalar, os dados do usuario
; original nao sao encontrados nem removidos; eles permanecem no perfil dele e podem ser apagados manualmente.
[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  RoamingDir, LocalDir, SnapDir: String;
begin
  if CurUninstallStep <> usPostUninstall then Exit;
  if UninstallSilent then Exit; // modo silencioso: nunca remove dados do usuario

  RoamingDir := ExpandConstant('{userappdata}\WinDiag');
  LocalDir := ExpandConstant('{localappdata}\WinDiag');
  SnapDir := ExpandConstant('{commonappdata}\WinDiagMaint\snapshots');

  if not (DirExists(RoamingDir) or DirExists(LocalDir)) then Exit;

  if MsgBox(FmtMessage(CustomMessage('AskRemoveUserData'), [RoamingDir, LocalDir, SnapDir]),
            mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
  begin
    if DirExists(RoamingDir) then DelTree(RoamingDir, True, True, True);
    if DirExists(LocalDir) then DelTree(LocalDir, True, True, True);
  end;
end;
