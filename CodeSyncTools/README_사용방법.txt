ESDP 코드 업데이트 도구
========================

이 도구는 개발 PC와 폐쇄망 PC 사이에서 애플리케이션 코드와 빌드에 필요한
프로젝트/리소스 파일을 동기화합니다.
NuGet 패키지와 개발환경 전체를 매번 다시 옮기기 위한 도구가 아닙니다.

[개발 PC - 내보내기]
1. CodeSyncTools 폴더의 Export_Code.cmd를 더블클릭합니다.
2. 프로젝트 폴더와 같은 위치의 ESDP_CodeUpdates 폴더에 날짜/시간이 붙은
   업데이트 폴더가 생성됩니다.
3. 생성된 ESDP_Code_Update_날짜_시간 폴더 전체를 USB 등으로 폐쇄망에 옮깁니다.

[폐쇄망 PC - 가져오기]
1. 폐쇄망 PC에 한 번만 보관해 둔 Import_Code.cmd와 Import-Code.ps1을
   같은 폴더에 둔 상태에서 Import_Code.cmd를 더블클릭합니다.
2. 이번에 옮긴 ESDP_Code_Update_날짜_시간 폴더를 선택합니다.
3. ESDP.csproj가 들어 있는 폐쇄망 프로젝트 폴더를 선택합니다.
4. 검증 결과와 삭제 예정 파일 수를 확인한 뒤 YES를 입력합니다.
5. Visual Studio에서 ESDP.csproj를 열어 빌드합니다.

※ 업데이트 폴더에는 Import_Code.cmd와 Import-Code.ps1이 포함되지 않습니다.
   폐쇄망에 보관한 Import 도구는 반복해서 사용합니다.

[동기화되는 항목]
- C# 코드, XAML, 앱 설정 및 리소스 정의
- ESDP.csproj와 Properties/PublishProfiles의 게시 설정
- Resources 폴더의 엑셀 템플릿, 이미지, 아이콘 및 글꼴
- Models 등의 하위 폴더 구조는 그대로 유지됩니다.
- 개발 PC에서 삭제되거나 이름이 변경된 관리 대상 소스는 폐쇄망에서도 삭제됩니다.

[보존되는 항목]
- ESDP.slnx
- NuGet.Config, Directory.Build.props, OfflineNuGet
- bin, obj, .vs, .git, .codex, 게시 결과
- 사용자 .esdp 프로젝트와 Test1 등의 작업 데이터

[안전장치]
- 가져오기 전에 현재 관리 대상 소스 전체가 프로젝트의
  .code-sync-backups\날짜_시간\Files 폴더에 자동 백업됩니다.
- 패키지 파일이 누락되거나 변조되면 실제 프로젝트를 변경하기 전에 중단합니다.
- 잘못된 프로젝트 폴더를 선택하면 ESDP.csproj 검사에서 중단합니다.

[중요]
- 개발 PC의 코드가 기준입니다. 폐쇄망에서만 따로 수정한 소스는 가져오기 시
  덮어써질 수 있으므로, 필요한 수정은 먼저 개발 PC 코드에 합쳐야 합니다.
- 새 NuGet 패키지가 추가되면 ESDP.csproj 변경은 코드 패키지에 포함되지만,
  해당 nupkg 파일은 별도로 폐쇄망의 OfflineNuGet에 반영해야 합니다.
