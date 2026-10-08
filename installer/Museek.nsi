; Native per-user setup, upgrade and uninstall. Generated includes list the exact payload.
Unicode True
RequestExecutionLevel user
ManifestDPIAware True
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "FileFunc.nsh"
!include "x64.nsh"
!include "WinVer.nsh"

!ifndef UPDATE_WAIT_TIMEOUT_MS
  !define UPDATE_WAIT_TIMEOUT_MS 30000
!endif
!if ${UPDATE_WAIT_TIMEOUT_MS} < 1
  !error "The update-parent wait timeout must be positive."
!endif
!if ${UPDATE_WAIT_TIMEOUT_MS} > 30000
  !error "The update-parent wait timeout cannot exceed 30000 milliseconds."
!endif

Name "${PRODUCT_NAME}"
OutFile "${SETUP_OUTPUT}"
InstallDir "${DEFAULT_INSTALL_DIR}"
InstallDirRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${PRODUCT_ID}" "InstallLocation"
SetCompressor /SOLID zlib
; VLC's plugin cache checks file sizes and modification times after extraction.
SetDateSave on
CRCCheck force
VIProductVersion "${APP_VERSION_QUAD}"
VIAddVersionKey /LANG=1033 "ProductName" "${PRODUCT_NAME} Setup"
VIAddVersionKey /LANG=1033 "FileDescription" "Install or update ${PRODUCT_NAME}"
VIAddVersionKey /LANG=1033 "FileVersion" "${APP_VERSION}"
VIAddVersionKey /LANG=1033 "ProductVersion" "${APP_VERSION}"
VIAddVersionKey /LANG=1033 "LegalCopyright" "Museek contributors"
!define MUI_ICON "${APP_ICON}"
!define MUI_UNICON "${APP_ICON}"
!define MUI_ABORTWARNING
!define MUI_WELCOMEPAGE_TITLE "${PRODUCT_NAME} Setup"
!define MUI_WELCOMEPAGE_TEXT "Install ${PRODUCT_NAME} for your Windows account.$\r$\n$\r$\nIf ${PRODUCT_NAME} is already installed, Setup updates that installation and keeps your settings.$\r$\n$\r$\nClose ${PRODUCT_NAME} before continuing."
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_RUN "$INSTDIR\Museek.exe"
!define MUI_FINISHPAGE_RUN_TEXT "Open ${PRODUCT_NAME}"
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_UNPAGE_FINISH
!insertmacro MUI_LANGUAGE "English"

!define ARP_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\${PRODUCT_ID}"
Var ExistingDirectory
Var ValidatedPath
Var FailureMessage
Var IsLegacy
Var ManifestPath
Var FileIndex
Var FileCount
Var RelativeFile
Var MutexHandle
Var RecoveryCount
Var MarkerStage
Var UpdateProcessHandle
Var UpdatePid

!include "${PAYLOAD_INCLUDE}"
!include "${MANIFEST_INCLUDE}"

!macro LegacyRecoveryFile Relative
  ClearErrors
  WriteINIStr "$PLUGINSDIR\recovery-files.ini" "Files" "$RecoveryCount" "${Relative}"
  WriteINIStr "$PLUGINSDIR\recovery-files.ini" "OwnedFiles" "${Relative}" "1"
  IfErrors install_failed
  IntOp $RecoveryCount $RecoveryCount + 1
!macroend

; Every runtime path is canonical and inside INSTDIR; ancestors cannot redirect it.
!macro ValidationFunctions Prefix
Function ${Prefix}CanonicalDirectory
  System::Call 'kernel32::GetFullPathNameW(w "$INSTDIR", i ${NSIS_MAX_STRLEN}, w .r5, p 0) i.r0'
  IntCmp $0 0 directory_invalid
  IntCmp $0 ${NSIS_MAX_STRLEN} directory_invalid directory_valid directory_invalid
directory_valid:
  StrCpy $INSTDIR $5
directory_trim:
  StrLen $0 $INSTDIR
  IntCmp $0 3 directory_done directory_done
  StrCpy $1 $INSTDIR 1 -1
  StrCmp $1 "\" 0 directory_done
  StrCpy $INSTDIR $INSTDIR -1
  Goto directory_trim
directory_done:
  Return
directory_invalid:
  StrCpy $FailureMessage "The installation folder could not be resolved or its path is too long."
  Call ${Prefix}Fail
FunctionEnd

Function ${Prefix}ValidatePath
  Pop $ValidatedPath
  System::Call 'kernel32::GetFullPathNameW(w "$ValidatedPath", i ${NSIS_MAX_STRLEN}, w .r5, p 0) i.r0'
  IntCmp $0 0 path_invalid
  IntCmp $0 ${NSIS_MAX_STRLEN} path_invalid path_canonical path_invalid
path_invalid:
  StrCpy $FailureMessage "An installation file path could not be resolved or is too long."
  Call ${Prefix}Fail
path_canonical:
  StrCpy $ValidatedPath $5
  StrLen $0 "$INSTDIR\"
  StrCpy $1 $ValidatedPath $0
  StrCmp $ValidatedPath $INSTDIR path_inside
  StrCmp $1 "$INSTDIR\" path_inside
  StrCpy $FailureMessage "An installation file path is outside the ${PRODUCT_NAME} folder."
  Call ${Prefix}Fail
path_inside:
  StrCpy $2 $ValidatedPath
ancestor_loop:
  System::Call 'kernel32::GetFileAttributesW(w r2) i.r3'
  IntCmp $3 -1 ancestor_parent
  IntOp $4 $3 & 0x400
  IntCmp $4 0 ancestor_directory
  StrCpy $FailureMessage "The installation folder contains a symbolic link or junction. Choose a regular folder."
  Call ${Prefix}Fail
ancestor_directory:
  StrCmp $2 $ValidatedPath ancestor_parent
  IntOp $4 $3 & 0x10
  IntCmp $4 0 ancestor_not_directory ancestor_parent ancestor_parent
ancestor_not_directory:
  StrCpy $FailureMessage "A required installation folder has been replaced by a file. Restore that folder before continuing."
  Call ${Prefix}Fail
ancestor_parent:
  ${GetParent} "$2" $3
  StrCmp $3 "" ancestor_done
  StrCmp $3 $2 ancestor_done
  StrCpy $2 $3
  Goto ancestor_loop
ancestor_done:
FunctionEnd

Function ${Prefix}CheckWritable
  IfFileExists "$ValidatedPath" 0 writable_done
  System::Call 'kernel32::GetFileAttributesW(w "$ValidatedPath") i.r0'
  IntOp $1 $0 & 0x11
  IntCmp $1 0 writable_probe
  StrCpy $FailureMessage "An installation file is write-protected or has been replaced by a folder."
  Call ${Prefix}Fail
writable_probe:
  System::Call 'kernel32::CreateFileW(w "$ValidatedPath", i 0x40000000, i 7, p 0, i 3, i 0x80, p 0) p.r0'
  IntCmp $0 -1 writable_failed
  System::Call 'kernel32::CloseHandle(p r0)'
  Goto writable_done
writable_failed:
  StrCpy $FailureMessage "${PRODUCT_NAME} files are in use. Close ${PRODUCT_NAME} and any tag editor windows, then run this program again."
  Call ${Prefix}Fail
writable_done:
FunctionEnd

Function ${Prefix}Fail
  !if "${Prefix}" == ""
    Call CloseUpdateParentHandle
  !endif
  FileOpen $0 "$TEMP\${PRODUCT_ID}.Setup-error.log" w
  FileWrite $0 "$FailureMessage$\r$\nInstall folder: $INSTDIR$\r$\n"
  FileClose $0
  MessageBox MB_OK|MB_ICONSTOP "$FailureMessage" /SD IDOK
  SetErrorLevel 1
  Abort "$FailureMessage"
FunctionEnd

; Only paths recorded by setup are removed. RMDir is deliberately nonrecursive.
Function ${Prefix}DeleteManagedFile
  Push "$INSTDIR\$RelativeFile"
  Call ${Prefix}ValidatePath
  ClearErrors
  IfFileExists "$ValidatedPath" 0 remove_parents
  Delete "$ValidatedPath"
  IfErrors 0 remove_parents
  StrCpy $FailureMessage "An installation file could not be removed. Close programs using ${PRODUCT_NAME} and try again."
  Call ${Prefix}Fail
remove_parents:
  ${GetParent} "$ValidatedPath" $2
remove_parent_loop:
  StrCmp $2 $INSTDIR remove_parent_done
  StrCmp $2 "" remove_parent_done
  RMDir "$2"
  ${GetParent} "$2" $3
  StrCmp $2 $3 remove_parent_done
  StrCpy $2 $3
  Goto remove_parent_loop
remove_parent_done:
  ClearErrors
FunctionEnd

Function ${Prefix}ReadManifest
  ReadINIStr $FileCount "$ManifestPath" "Files" "Count"
  StrCmp $FileCount "" manifest_missing
  IntOp $0 $FileCount + 0
  StrCmp $0 $FileCount 0 manifest_invalid
  IntCmp $FileCount 0 manifest_invalid manifest_invalid
  IntCmp $FileCount 100000 manifest_ready manifest_ready manifest_invalid
manifest_invalid:
  StrCpy $FailureMessage "The installation file list is invalid. Run Setup to repair ${PRODUCT_NAME}."
  Call ${Prefix}Fail
manifest_missing:
  IfFileExists "$ManifestPath" manifest_invalid
  StrCpy $FileCount 0
manifest_ready:
  StrCpy $FileIndex 0
  ClearErrors
FunctionEnd

Function ${Prefix}CheckManifestFiles
  Call ${Prefix}ReadManifest
check_file_loop:
  IntCmp $FileIndex $FileCount check_file_done check_file_next check_file_done
check_file_next:
  ReadINIStr $RelativeFile "$ManifestPath" "Files" "$FileIndex"
  StrCmp $RelativeFile "" check_file_bad
  Push "$INSTDIR\$RelativeFile"
  Call ${Prefix}ValidatePath
  Call ${Prefix}CheckWritable
  IntOp $FileIndex $FileIndex + 1
  Goto check_file_loop
check_file_bad:
  StrCpy $FailureMessage "The installation file list is incomplete. Run Setup to repair ${PRODUCT_NAME}."
  Call ${Prefix}Fail
check_file_done:
FunctionEnd
!macroend
!insertmacro ValidationFunctions ""
!insertmacro ValidationFunctions "un."

; Commit a complete ownership marker atomically, keeping an existing marker valid.
Function WriteMarker
  StrCpy $MarkerStage ""
  ClearErrors
  System::Call 'kernel32::GetTempFileNameW(w "$INSTDIR", w "msk", i 0, w .r5) i.r0'
  IntCmp $0 0 marker_failed
  StrCpy $MarkerStage $5
  Push "$MarkerStage"
  Call ValidatePath
  FileOpen $0 "$MarkerStage" w
  IfErrors marker_failed
  FileWriteWord $0 0xFEFF
  FileClose $0
  IfErrors marker_failed
  WriteINIStr "$MarkerStage" "Install" "ProductId" "${PRODUCT_ID}"
  WriteINIStr "$MarkerStage" "Install" "Directory" "$INSTDIR"
  WriteINIStr "$MarkerStage" "Install" "Version" "${APP_VERSION}"
  IfErrors marker_failed
  ; Flushing an INI cache returns zero even on success.
  System::Call 'kernel32::WritePrivateProfileStringW(p 0, p 0, p 0, w "$MarkerStage") i.r0'
  System::Call 'kernel32::MoveFileExW(w "$MarkerStage", w "$INSTDIR\.museek-install.ini", i 9) i.r0'
  IntCmp $0 0 marker_failed
  StrCpy $MarkerStage ""
  ClearErrors
  Return
marker_failed:
  StrCmp $MarkerStage "" marker_report
  Delete "$MarkerStage"
marker_report:
  StrCpy $FailureMessage "The installation record could not be saved. Run Setup again to repair ${PRODUCT_NAME}."
  Call Fail
FunctionEnd

Function CloseUpdateParentHandle
  StrCmp $UpdateProcessHandle 0 update_handle_done
  System::Call 'kernel32::CloseHandle(p $UpdateProcessHandle)'
  StrCpy $UpdateProcessHandle 0
update_handle_done:
FunctionEnd

; Capture before the welcome page: an open handle continues to identify the
; original process even if its numeric PID is later recycled.
Function CaptureUpdateParent
  StrCpy $UpdateProcessHandle 0
  ${GetParameters} $0
  ClearErrors
  ${GetOptions} "$0" "/UPDATEPID" $1
  IfErrors update_parent_done
  StrCpy $2 $1 1
  StrCmp $2 "=" 0 update_pid_invalid
  StrCpy $UpdatePid $1 "" 1
  StrCmp $UpdatePid "" update_pid_invalid
  IntOp $2 $UpdatePid + 0
  StrCmp $2 $UpdatePid 0 update_pid_invalid
  IntCmp $2 0 update_pid_invalid update_pid_invalid
  ; Reject duplicate options instead of choosing an ambiguous parent.
  StrCpy $3 0
  StrCpy $5 0
update_pid_scan:
  StrCpy $4 $0 1 $3
  StrCmp $4 "" update_pid_open
  StrCpy $4 $0 10 $3
  StrCmp $4 "/UPDATEPID" 0 update_pid_next
  IntOp $5 $5 + 1
  StrCmp $5 1 0 update_pid_invalid
  ; GetOptions treats adjacent slashes as option delimiters and trims spaces.
  ; Require the original argument to contain exactly '=digits' as one token.
  IntOp $6 $3 + 10
  StrCpy $7 $0 1 $6
  StrCmp $7 "=" 0 update_pid_invalid
  IntOp $6 $6 + 1
  StrLen $7 $UpdatePid
  StrCpy $8 $0 $7 $6
  StrCmp $8 $UpdatePid 0 update_pid_invalid
  IntOp $6 $6 + $7
  StrCpy $8 $0 1 $6
  StrCmp $8 "" update_pid_next
  StrCmp $8 " " update_pid_next
  StrCmp $8 "$\t" update_pid_next
  Goto update_pid_invalid
update_pid_next:
  IntOp $3 $3 + 1
  Goto update_pid_scan
update_pid_open:
  ; SYNCHRONIZE grants only the right to observe process exit; no termination.
  System::Call 'kernel32::OpenProcess(i 0x00100000, i 0, i $UpdatePid) p.r0 ?e'
  Pop $1
  StrCpy $UpdateProcessHandle $0
  StrCmp $0 0 0 update_parent_done
  ; A validated nonzero PID can already have exited before setup starts.
  IntCmp $1 87 update_parent_done
  StrCpy $FailureMessage "Setup could not confirm that ${PRODUCT_NAME} has closed. Close ${PRODUCT_NAME}, then run Setup again."
  Call Fail
update_pid_invalid:
  StrCpy $FailureMessage "Setup received an invalid update process ID. Run Setup again from ${PRODUCT_NAME} or without update arguments."
  Call Fail
update_parent_done:
  ClearErrors
FunctionEnd

Function WaitForUpdateParent
  StrCmp $UpdateProcessHandle 0 update_wait_done
  DetailPrint "Waiting for ${PRODUCT_NAME} to close..."
  System::Call 'kernel32::WaitForSingleObject(p $UpdateProcessHandle, i ${UPDATE_WAIT_TIMEOUT_MS}) i.r0'
  Call CloseUpdateParentHandle
  IntCmp $0 0 update_wait_done
  IntCmp $0 258 update_wait_timeout
  StrCpy $FailureMessage "Setup could not confirm that ${PRODUCT_NAME} has closed. Close ${PRODUCT_NAME}, then run Setup again."
  Call Fail
update_wait_timeout:
  StrCpy $FailureMessage "${PRODUCT_NAME} is still closing. Close ${PRODUCT_NAME} and any tag editor windows, then run Setup again."
  Call Fail
update_wait_done:
FunctionEnd

Function .onGUIEnd
  Call CloseUpdateParentHandle
FunctionEnd

Function .onInstSuccess
  Call CloseUpdateParentHandle
FunctionEnd

Function .onInstFailed
  Call CloseUpdateParentHandle
FunctionEnd

Function .onInit
  StrCpy $UpdateProcessHandle 0
  Call CaptureUpdateParent
  SetShellVarContext current
  SetRegView 64
  ${IfNot} ${RunningX64}
    StrCpy $FailureMessage "${PRODUCT_NAME} requires Windows 11 x64."
    Call Fail
  ${EndIf}
  ${IfNot} ${AtLeastWin11}
    StrCpy $FailureMessage "${PRODUCT_NAME} requires Windows 11."
    Call Fail
  ${EndIf}
  System::Call 'kernel32::CreateMutexW(p 0, i 0, w "Local\${PRODUCT_ID}.Setup") p.r0 ?e'
  Pop $1
  StrCpy $MutexHandle $0
  IntCmp $1 183 setup_already_running
  ReadRegStr $ExistingDirectory HKCU "${ARP_KEY}" "InstallLocation"
  StrCmp $ExistingDirectory "" new_location
  ; Installed location takes priority over /D so rerunning setup cannot create another copy.
  StrCpy $INSTDIR $ExistingDirectory
new_location:
  Call CanonicalDirectory
  ${GetRoot} "$INSTDIR" $0
  StrCmp $INSTDIR $0 unsafe_location
  StrCmp $INSTDIR "$0\" unsafe_location
  Push "$INSTDIR"
  Call ValidatePath
  Push "$INSTDIR\.museek-install.ini"
  Call ValidatePath
  Call CheckWritable
  Push "$INSTDIR\.museek-files.ini"
  Call ValidatePath
  Call CheckWritable
  Push "$INSTDIR\.museek-install.json"
  Call ValidatePath
  Call CheckWritable
  Return
setup_already_running:
  StrCpy $FailureMessage "Another ${PRODUCT_NAME} installation or uninstall is already running."
  Call Fail
unsafe_location:
  StrCpy $FailureMessage "The installation folder must be a dedicated ${PRODUCT_NAME} folder."
  Call Fail
FunctionEnd

Section "Install or update"
  ; Wait only once installation starts. Existing lock/ownership checks below
  ; still reject another running instance before changing any installed files.
  Call WaitForUpdateParent
  StrCpy $IsLegacy 0
  IfFileExists "$INSTDIR\.museek-install.ini" native_install
  IfFileExists "$INSTDIR\*.*" existing_folder fresh_install
existing_folder:
  ${DirState} "$INSTDIR" $0
  StrCmp $0 "0" fresh_install
  IfFileExists "$INSTDIR\.museek-install.json" 0 foreign_folder
  StrCmp $ExistingDirectory $INSTDIR 0 foreign_folder
  IfFileExists "$INSTDIR\Museek.exe" 0 foreign_folder
  System::Call 'kernel32::SetEnvironmentVariableW(w "MUSEEK_SETUP_TARGET", w "$INSTDIR")'
  nsExec::ExecToStack /TIMEOUT=15000 '$\"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe$\" -NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand ${LEGACY_VALIDATION}'
  Pop $0
  Pop $1
  StrCmp $0 "0" 0 foreign_folder
  Push "$INSTDIR\Install.cmd"
  Call ValidatePath
  Call CheckWritable
  Push "$INSTDIR\Install.ps1"
  Call ValidatePath
  Call CheckWritable
  Push "$INSTDIR\Uninstall.ps1"
  Call ValidatePath
  Call CheckWritable
  StrCpy $IsLegacy 1
  Goto fresh_install
native_install:
  ReadINIStr $0 "$INSTDIR\.museek-install.ini" "Install" "ProductId"
  StrCmp $0 "${PRODUCT_ID}" 0 foreign_folder
  ReadINIStr $0 "$INSTDIR\.museek-install.ini" "Install" "Directory"
  StrCmp $0 $INSTDIR 0 foreign_folder
fresh_install:
  ; Check every old and new managed file before changing any existing bytes.
  StrCpy $ManifestPath "$INSTDIR\.museek-files.ini"
  Call CheckManifestFiles
  !insertmacro MuseekCheckPayload
  Push "$INSTDIR\uninstall.exe"
  Call ValidatePath
  Call CheckWritable
  InitPluginsDir
  !insertmacro MuseekWriteManifest "$PLUGINSDIR\new-files.ini"
  IfFileExists "$ManifestPath" 0 previous_manifest_saved
  CopyFiles /SILENT "$ManifestPath" "$PLUGINSDIR\previous-files.ini"
  IfErrors install_failed
previous_manifest_saved:
  ; Record all old/new managed paths before extraction so interrupted setup can be repaired.
  CopyFiles /SILENT "$PLUGINSDIR\new-files.ini" "$PLUGINSDIR\recovery-files.ini"
  IfErrors install_failed
  ReadINIStr $RecoveryCount "$PLUGINSDIR\new-files.ini" "Files" "Count"
  Call ReadManifest
recovery_loop:
  IntCmp $FileIndex $FileCount recovery_done recovery_next recovery_done
recovery_next:
  ReadINIStr $RelativeFile "$ManifestPath" "Files" "$FileIndex"
  ReadINIStr $0 "$PLUGINSDIR\recovery-files.ini" "OwnedFiles" "$RelativeFile"
  StrCmp $0 "1" recovery_kept
  ClearErrors
  WriteINIStr "$PLUGINSDIR\recovery-files.ini" "Files" "$RecoveryCount" "$RelativeFile"
  WriteINIStr "$PLUGINSDIR\recovery-files.ini" "OwnedFiles" "$RelativeFile" "1"
  IfErrors install_failed
  IntOp $RecoveryCount $RecoveryCount + 1
recovery_kept:
  IntOp $FileIndex $FileIndex + 1
  Goto recovery_loop
recovery_done:
  ${If} $IsLegacy == 1
    !insertmacro LegacyRecoveryFile "Install.cmd"
    !insertmacro LegacyRecoveryFile "Install.ps1"
    !insertmacro LegacyRecoveryFile "Uninstall.ps1"
    !insertmacro LegacyRecoveryFile ".museek-install.json"
  ${EndIf}
  WriteINIStr "$PLUGINSDIR\recovery-files.ini" "Files" "Count" "$RecoveryCount"
  IfErrors install_failed
  ClearErrors
  SetOutPath "$INSTDIR"
  IfErrors install_failed
  ; Keep the validated marker intact while an existing installation is updated.
  IfFileExists "$INSTDIR\.museek-install.ini" marker_ready
  Call WriteMarker
marker_ready:
  CopyFiles /SILENT "$PLUGINSDIR\recovery-files.ini" "$INSTDIR\.museek-files.ini"
  IfErrors install_failed
  ClearErrors
  !insertmacro MuseekInstallPayload
  WriteUninstaller "$INSTDIR\uninstall.exe"
  IfErrors install_failed
  ; Cleanup compares the old manifest with the successfully extracted new payload.
  StrCpy $ManifestPath "$PLUGINSDIR\previous-files.ini"
  Call ReadManifest
obsolete_loop:
  IntCmp $FileIndex $FileCount obsolete_done obsolete_next obsolete_done
obsolete_next:
  ReadINIStr $RelativeFile "$ManifestPath" "Files" "$FileIndex"
  ReadINIStr $0 "$PLUGINSDIR\new-files.ini" "OwnedFiles" "$RelativeFile"
  StrCmp $0 "1" obsolete_kept
  Call DeleteManagedFile
obsolete_kept:
  IntOp $FileIndex $FileIndex + 1
  Goto obsolete_loop
obsolete_done:
  SetOutPath "$INSTDIR"
  IfErrors install_failed
  CopyFiles /SILENT "$PLUGINSDIR\new-files.ini" "$INSTDIR\.museek-files.ini"
  IfErrors install_failed
  Call WriteMarker
  ${If} $IsLegacy == 1
    Delete "$INSTDIR\Install.cmd"
    Delete "$INSTDIR\Install.ps1"
    Delete "$INSTDIR\Uninstall.ps1"
    Delete "$INSTDIR\.museek-install.json"
  ${EndIf}
  CreateShortcut "$SMPROGRAMS\${PRODUCT_NAME}.lnk" "$INSTDIR\Museek.exe" "" "$INSTDIR\Museek.exe" 0
  WriteRegStr HKCU "${ARP_KEY}" "DisplayName" "${PRODUCT_NAME}"
  WriteRegStr HKCU "${ARP_KEY}" "DisplayVersion" "${APP_VERSION}"
  WriteRegStr HKCU "${ARP_KEY}" "DisplayIcon" "$INSTDIR\Museek.exe,0"
  WriteRegStr HKCU "${ARP_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${ARP_KEY}" "UninstallString" '$\"$INSTDIR\uninstall.exe$\"'
  WriteRegStr HKCU "${ARP_KEY}" "QuietUninstallString" '$\"$INSTDIR\uninstall.exe$\" /S'
  WriteRegDWORD HKCU "${ARP_KEY}" "EstimatedSize" ${PAYLOAD_KB}
  WriteRegDWORD HKCU "${ARP_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${ARP_KEY}" "NoRepair" 1
  IfErrors install_failed
  ExecWait '$\"$INSTDIR\Museek.exe$\" --register --quiet' $0
  IfErrors install_failed
  IntCmp $0 0 install_done
install_failed:
  StrCpy $FailureMessage "${PRODUCT_NAME} could not finish installation. Close programs using it and run Setup again to repair the installation."
  Call Fail
foreign_folder:
  StrCpy $FailureMessage "This folder contains files from another installation. Setup will not overwrite them."
  Call Fail
install_done:
  SetErrorLevel 0
SectionEnd

Function un.onInit
  SetShellVarContext current
  SetRegView 64
  Call un.CanonicalDirectory
  System::Call 'kernel32::CreateMutexW(p 0, i 0, w "Local\${PRODUCT_ID}.Setup") p.r0 ?e'
  Pop $1
  StrCpy $MutexHandle $0
  IntCmp $1 183 uninstall_already_running
  Push "$INSTDIR"
  Call un.ValidatePath
  Push "$INSTDIR\.museek-install.ini"
  Call un.ValidatePath
  Call un.CheckWritable
  Push "$INSTDIR\.museek-files.ini"
  Call un.ValidatePath
  Call un.CheckWritable
  ReadINIStr $0 "$INSTDIR\.museek-install.ini" "Install" "ProductId"
  StrCmp $0 "${PRODUCT_ID}" 0 invalid_install
  ReadINIStr $0 "$INSTDIR\.museek-install.ini" "Install" "Directory"
  StrCmp $0 $INSTDIR 0 invalid_install
  StrCpy $ManifestPath "$INSTDIR\.museek-files.ini"
  IfFileExists "$ManifestPath" 0 invalid_install
  Call un.CheckManifestFiles
  Return
uninstall_already_running:
  StrCpy $FailureMessage "Another ${PRODUCT_NAME} installation or uninstall is already running."
  Call un.Fail
invalid_install:
  StrCpy $FailureMessage "This is not a valid ${PRODUCT_NAME} installation. Run Setup to repair it before uninstalling."
  Call un.Fail
FunctionEnd

Section "Uninstall"
  SetOutPath "$TEMP"
  ExecWait '$\"$INSTDIR\Museek.exe$\" --unregister --quiet' $0
  IfErrors uninstall_failed
  IntCmp $0 0 unregister_done
uninstall_failed:
  StrCpy $FailureMessage "Windows registration cleanup failed. Run Setup to repair ${PRODUCT_NAME}, then uninstall again."
  Call un.Fail
unregister_done:
  Call un.ReadManifest
uninstall_file_loop:
  IntCmp $FileIndex $FileCount uninstall_files_done uninstall_file_next uninstall_files_done
uninstall_file_next:
  ReadINIStr $RelativeFile "$ManifestPath" "Files" "$FileIndex"
  Call un.DeleteManagedFile
  IntOp $FileIndex $FileIndex + 1
  Goto uninstall_file_loop
uninstall_files_done:
  ReadRegStr $0 HKCU "${ARP_KEY}" "InstallLocation"
  ${If} $0 == $INSTDIR
    ClearErrors
    Delete "$SMPROGRAMS\${PRODUCT_NAME}.lnk"
    IfErrors uninstall_control_failed
    DeleteRegKey HKCU "${ARP_KEY}"
    IfErrors uninstall_control_failed
  ${EndIf}
  ClearErrors
  Delete "$INSTDIR\.museek-files.ini"
  IfErrors uninstall_control_failed
  Delete "$INSTDIR\.museek-install.ini"
  IfErrors uninstall_control_failed
  Delete "$INSTDIR\uninstall.exe"
  RMDir "$INSTDIR"
  SetErrorLevel 0
  Goto uninstall_complete
uninstall_control_failed:
  StrCpy $FailureMessage "An installer control file could not be removed. Run Setup to repair ${PRODUCT_NAME}."
  Call un.Fail
uninstall_complete:
SectionEnd
