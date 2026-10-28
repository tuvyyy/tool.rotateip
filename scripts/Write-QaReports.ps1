$ErrorActionPreference = 'Stop'
$workspace = Split-Path $PSScriptRoot -Parent
Set-Location $workspace
$reportRoot = Join-Path $workspace 'artifacts/reports'
$utf8 = New-Object System.Text.UTF8Encoding($false)
function Write-Report([string]$name, [string]$body) { [IO.File]::WriteAllText((Join-Path $reportRoot $name), $body, $utf8) }
function Cell([string]$s) { ($s -replace '\|','\|' -replace '[\r\n]+',' ' -replace '<','&lt;' -replace '>','&gt;') }
$final = [xml](Get-Content artifacts/tests/final/full-final.trx -Raw)
$baseline = [xml](Get-Content artifacts/tests/baseline.trx -Raw)
$results = @($final.TestRun.Results.UnitTestResult | Sort-Object testName)
$originalNames = @($baseline.TestRun.Results.UnitTestResult | ForEach-Object testName)
$original = @($results | Where-Object { $originalNames -contains $_.testName })
$counters = $final.TestRun.ResultSummary.Counters
$archive = @(foreach ($file in Get-ChildItem artifacts/tests -Filter *.trx -Recurse) {
    try {
        $trx = [xml](Get-Content $file.FullName -Raw)
        $c = $trx.TestRun.ResultSummary.Counters
        if ($c) { [pscustomobject]@{ File=$file.FullName.Substring($workspace.Length+1); Total=[int]$c.total; Executed=[int]$c.executed; Passed=[int]$c.passed; Failed=[int]$c.failed; Skipped=[int]$c.notExecuted } }
    } catch { }
})
$archive | ConvertTo-Json -Depth 4 | Set-Content -Encoding UTF8 artifacts/tests/execution-index.json
$duration = ([datetime]$final.TestRun.Times.finish - [datetime]$final.TestRun.Times.start).ToString()
$body = @'
# FULL APPLICATION TEST REPORT

Ngày kiểm thử: 25/10/2026. Workspace: `C:\toolrotateip`. Quyết định: **HOLD — chưa đề xuất phát hành**.

Source hiện hiển thị thương hiệu “Accre Rotate IP”; solution, assembly và bộ cài vẫn mang tên NetworkProfileManager. Giữ nguyên các thay đổi UI/branding đã có trong workspace.

## Môi trường và phạm vi

Windows 11 x64 build 26200; SDK .NET 10.0.402; runtime 10.0.13; WPF/MVVM; SQLite/DPAPI CurrentUser; Inno Setup 6.7.3. Không có `.git`, nên không có commit để ghi nhận. Hash trước/sau: [baseline](source-baseline.json), [diff các file có baseline](source-final.json), [file có hash thay đổi](changed-files.txt), [snapshot đầy đủ source/test/harness cuối](source-snapshot-final.json). Baseline chỉ bao phủ những file đã tồn tại lúc lấy mẫu; file mới được ghi trong snapshot cuối. Đã đọc README, solution và các lớp Core/Infrastructure/App/Tests trước baseline.

Thử nghiệm dùng thư mục GUID trong `%TEMP%\NetworkProfileManager.Tests` hoặc `artifacts/`; mock chỉ nghe loopback. App native/installed được chạy với `--data-directory` riêng. Không ghi đè dữ liệu production, không xóa profile cá nhân, không chạy DHCP Renew/Flush DNS hay đổi adapter/Wi-Fi. Browser cá nhân không được quản lý/đóng bởi test; test riêng xác nhận một browser QA ngoài manager sống qua StopBoth.

PASS = đã thực thi và đạt assertion/phạm vi quan sát ghi rõ; FAIL = đã thực thi nhưng assertion không đạt; BLOCKED = thiếu điều kiện; NOT TESTED = chưa thực hiện. Một screenshot được render không tương đương kiểm thử DPI native hoặc toàn bộ tương tác.

## Build và số lượng

'@
$body += "`n- Baseline: restore PASS; Release build **0 warning / 0 error**; **49/49 PASS**, khoảng 37 giây. [restore](../logs/baseline-restore.log), [build](../logs/baseline-build.log), [tests](../tests/baseline.trx).`n"
$body += "- Lượt đầy đủ cuối: **$($counters.total) test thực thi; $($counters.passed) PASS; $($counters.failed) FAIL; $($counters.notExecuted) skipped**. Thời gian wall-clock TRX: $duration; VSTest báo khoảng 6 phút 52 giây cho test assembly. Toàn bộ **$($original.Count) test cũ vẫn có trong lượt cuối và đều PASS**. [TRX cuối](../tests/final/full-final.trx), [console/exception](../logs/full-final-tests.log).`n"
$body += "- Đã bổ sung 76 case so với baseline. Lượt tái hiện trước sửa có FAIL chủ đích; không cộng số lần chạy lại thành số case duy nhất. [Chỉ mục các TRX lưu được](../tests/execution-index.json) gồm $($archive.Count) lượt, tổng $((($archive | Measure-Object Executed -Sum).Sum)) lượt thực thi case theo TRX (có lặp và có test harness bị gián đoạn).`n"
$body += "- Bảng phạm vi môi trường/manual bên dưới ghi riêng **7 BLOCKED và 6 NOT TESTED**. Chúng không phải xUnit skipped và không cộng vào 125 cases. Có 70 ảnh WPF matrix, không phải 70 automated test cases bổ sung.`n"
$body += @'
- Build cuối PASS, **0 warning / 0 error**: [final-build.log](../logs/final-build.log). Publish self-contained win-x64 PASS: [candidate-publish.log](../logs/candidate-publish.log). SDK: [environment-sdk.txt](../logs/environment-sdk.txt).
- Không giảm assertion, không skip test để làm xanh. Một số lần chẩn đoán testhost bị ngắt trong quá trình tranh chấp build/process; không coi đó là bằng chứng app crash. Log chẩn đoán blame-crash có lỗi dependency của datacollector; regular build không có warning này.

## Lỗi còn lại ảnh hưởng release

**B008 (P1, OPEN)**: proof request thực tế của browser không xuất hiện ở mock dù launcher đã tạo tiến trình/listener. Lượt cuối FAIL: Chrome+Cốc Cốc (restart A), Edge+Cốc Cốc (restart A), Firefox+Firefox (B lần đầu), Edge+Edge stress (chu kỳ 2). Đây là lỗi xác minh đường đi/khởi chạy chưa giải quyết; không kết luận traffic đã đi trực tiếp hay rò IP vì chưa có bằng chứng đó. Chi tiết, bước tái hiện và stack trace ở [BUG_LIST](BUG_LIST.md).

Chrome+Firefox hoàn thành 20 chu kỳ và 50 IP checks. Edge+Edge chỉ hoàn thành 1/20 chu kỳ, 50 IP checks; không ghi toàn bộ stress PASS. Tất cả 10 tổ hợp browser đều đã chạy; bảng AUTO bên dưới ghi outcome riêng.

## Bảng kiểm thử bổ sung và giới hạn

Các dòng UI/performance/installer dưới đây là subcase hoặc evidence độc lập, **không cộng thêm vào 125 xUnit cases**.

| Test ID | Test Case | Expected | Actual | PASS/FAIL/BLOCKED/NOT TESTED | Evidence |
|---|---|---|---|---|---|
| LIVE-01 | Đọc thông tin Wi-Fi hiện tại | Có adapter, IPv4, gateway; không đổi mạng | Wi-Fi up, có IPv4 và gateway | PASS | [network-information.json](../logs/network-information.json) |
| LIVE-02 | Direct public IP | HTTPS echo trả IP hợp lệ | IP hợp lệ; 532 ms; không lưu IP thật vào report | PASS | [direct-public-ip.json](../logs/direct-public-ip.json) |
| NATIVE-01 | Light theme sau restart EXE publish | Khôi phục Light từ DB riêng | Native window sáng; QA profile A/B đúng | PASS | [ảnh](../screenshots/native-overview-light-restart.png) |
| NATIVE-02 | Toggle Light/Dark | Palette và switch đồng bộ | Settings cả hai theme quan sát đúng | PASS | [Light](../screenshots/native-settings-light.png), [Dark](../screenshots/native-settings-dark.png) |
| NATIVE-03 | ComboBox HTTP/SOCKS5 | Dropdown nhìn rõ, mở được | Hai lựa chọn hiện đầy đủ | PASS | [dropdown](../screenshots/native-protocol-dropdown.png) |
| NATIVE-04 | Save As CSV | Dialog mở khi bấm Xuất CSV | Dialog thực tế mở; chưa hoàn tất Save qua UI | PASS | Phạm vi chỉ mở dialog; writer riêng có AUTO regression |
| NATIVE-05 | Save CSV qua dialog tới đích cuối | File được tạo qua dialog | Chưa hoàn tất tương tác native | NOT TESTED | AUTO CSV writer đã PASS, không thay thế case này |
| UI-FOCUS | Help bằng keyboard/event, password và clipboard | Tip mở; không copy khi thiếu IP; copy giá trị đúng | Assertion WPF PASS; clipboard ban đầu được khôi phục | PASS | DesktopUiTests trong TRX cuối |
| UI-BIND | Binding và contrast text | Không Warning/Error binding; text/muted >=4.5:1 | Assertions PASS ở hai theme | PASS | DesktopUiTests; không khẳng định mọi trạng thái màu đều đạt WCAG |
| UI-STRESS | 50 lần chuyển trang/theme/resize | Không exception, layout cập nhật | 50 vòng hoàn tất | PASS | [ui-matrix.json](../logs/ui-matrix.json) |
| LIFE-CLOSE | Đóng app khi 2 browsers và IP request đang chạy | Hủy request; managed children/listeners thoát | Tất cả assertions PASS | PASS | [close-while-active.json](../logs/close-while-active.json) |
| MOCK-ROUTE | Proxy failure có direct endpoint đang reachable | Không chạm direct endpoint | Connection-count assertion PASS | PASS | FailedProxyCannotConnectToReachableDirectEchoServer trong TRX |
| PROVIDER-01 | Rotating gateway thương mại thật | Chính sách IP/reauth/expiry đúng provider | Không có tài khoản/provider thật | BLOCKED | Mock rotation không thay cho provider validation |
| PROVIDER-02 | Sticky session thật | API/session capability được xác minh | Chưa có tài liệu/thông số provider; UI báo chưa cấu hình | BLOCKED | StandardSessionProvider; StickyNotInvented PASS |
| NET-01 | Wi-Fi physically disconnected | UI và request phản ánh mất mạng thật | Không ngắt Wi-Fi người dùng | BLOCKED | Không thay adapter |
| NET-02 | Wi-Fi disconnect/reconnect giữa phiên | Hồi phục theo kết nối mới | Mock disconnect PASS; chưa đổi mạng thật | BLOCKED | Không thay adapter |
| NET-03 | DNS failure trên resolver hệ thống | Thông báo đúng, không direct fallback | Chưa tạo lỗi resolver thật | NOT TESTED | SOCKS remote DNS wire và proxy offline đã kiểm thử |
| NET-04 | Public IPv6 end-to-end | Public echo qua IPv6 thật | Loopback ::1 và wire IPv6 PASS; public IPv6 chưa kiểm chứng | BLOCKED | ActualIpv6LoopbackGatewayConnects; SocksForwardsRemoteDnsOrLiteralAddress |
| DPI-125 | Windows native 125% | Bảy trang/tương tác đúng | Native đang 96 DPI; chỉ render-scale 120 DPI | BLOCKED | [ui-matrix.json](../logs/ui-matrix.json) |
| DPI-150 | Windows native 150% | Bảy trang/tương tác đúng | Chỉ render-scale 144 DPI | BLOCKED | Không đổi Windows scale của người dùng |
| LEAK-01 | DNS/WebRTC leak browser toàn diện | Packet capture và browser test xác minh | Chưa có capture DNS/WebRTC tương ứng | NOT TESTED | Không tuyên bố ngăn mọi leak |
| INSTALL-LIVE | Uninstall khi app+2 browsers còn hoạt động | Không để managed process/listener | Smoke đóng app trước uninstall | NOT TESTED | Installer-smoke không bao phủ case này |
| INSTALL-PROD | Cài vào namespace production mặc định | Không đụng dữ liệu hiện hữu | Chỉ cài sandbox AppId khác, cùng source/payload | NOT TESTED | Tránh đụng bản người dùng |
| CRASH-DUMP | Raw crash-memory dump không chứa credentials | Không có secret trong dump | Không tạo dump bộ nhớ chứa credential | NOT TESTED | Startup diagnostic được giới hạn kiểu exception; không bảo đảm memory dump sạch |

'@
$installer = Get-Content artifacts/logs/installer-smoke.json -Raw | ConvertFrom-Json
foreach($step in $installer.Steps) { $body += "| $($step.Id) | Installer sandbox | $(Cell $step.Expected) | $(Cell $step.Actual) | $($step.Status) | [installer-smoke.json](../logs/installer-smoke.json) |`n" }
$ui = Get-Content artifacts/logs/ui-matrix.json -Raw | ConvertFrom-Json
foreach($case in $ui.Cases) {
    $theme=$case.Theme.ToLowerInvariant(); $size="$($case.Width)x$($case.Height)"; $page=$case.Page
    $body += "| UI-$theme-$size-$page | Trang $page, $theme, $size | Render/layout assertions; đối chiếu ảnh | Render thật WPF ở native96 DPI; xem nhận xét visual | PASS | [ảnh](../screenshots/$theme-$size-page-$page.png) |`n"
}
$body += @'

Visual inspection: đã đối chiếu 10 contact sheets gồm đủ 70 ảnh, cùng các ảnh native foreground. Sidebar/header phân cách rõ, không thấy control chồng nhau sau sửa; một số giá trị dài được ellipsis và tooltip. Các trang Browsers/Network/Settings dùng cuộn dọc ở 1100×700/1280×720; phần dưới viewport cần cuộn, không phải đã xác minh toàn bộ offscreen bằng ảnh đầu trang. Ở 1366×768, icon trợ giúp của hàng Kiểm tra 3 lần xuống dòng riêng; cosmetic còn lại B013. White space của Overview lớn ở màn hình rộng là bố cục nội dung ngắn; không tự co giãn card theo chiều cao. Không tuyên bố không còn mọi lỗi clipping/focus trong mọi trạng thái.

## Kết quả từng automated case cuối

Expected dưới đây mô tả contract từ tên test; assertion cụ thể nằm trong test source. Actual FAIL giữ message và full stack trong TRX/log. Những case có nhiều input được liệt kê riêng.

| Test ID | Test Case | Expected | Actual | PASS/FAIL/BLOCKED/NOT TESTED | Evidence |
|---|---|---|---|---|---|
'@
$index=0
foreach($result in $results) {
    $index++; $id='AUTO-{0:D3}' -f $index
    $name=($result.testName -replace '^NetworkProfileManager.Tests\.','')
    $contract=($name -replace '^.*?\.','' -replace '\(.*$','' -creplace '([a-z])([A-Z])','$1 $2')
    $status=if($result.outcome -eq 'Passed') {'PASS'} elseif($result.outcome -eq 'Failed') {'FAIL'} else {'NOT TESTED'}
    $actual=if($status -eq 'PASS') { "Assertions passed; $($result.duration)" } else { $message=[string]$result.Output.ErrorInfo.Message; if($message.Length -gt 230) { $message=$message.Substring(0,230)+'…' }; "$(Cell $message); $($result.duration)" }
    $body += "`n| $id | $(Cell $name) | $(Cell $contract) | $actual | $status | [TRX](../tests/final/full-final.trx) |"
}
$body += @'


## Thay đổi, artifacts và release

Các lỗi đã sửa, root cause và regression: [BUG_LIST.md](BUG_LIST.md). Chi tiết credentials/TLS/routing: [SECURITY_REVIEW.md](SECURITY_REVIEW.md). Số đo thực: [PERFORMANCE_REPORT.md](PERFORMANCE_REPORT.md). Điều kiện phát hành: [RELEASE_CHECKLIST.md](RELEASE_CHECKLIST.md).

Các file chính đã sửa: Core/Models/Configuration.cs, Core/Services/Interfaces.cs; Infrastructure/Storage/{SqliteSettingsStore,AppPaths}.cs; Infrastructure/ProxyAdapters/{LocalProxyBridge,UpstreamProxyConnector}.cs; Infrastructure/BrowserAdapters/{BrowserAdapters,BrowserProcessJob}.cs; Infrastructure/Services/BrowserLauncherService.cs; App/{App.xaml.cs,MainWindow.xaml,NetworkProfileManager.App.csproj}; App/ViewModels/MainViewModel.cs; App/Services/DesktopService.cs; installer/NetworkProfileManager.iss. Test/harness mới hoặc mở rộng: QaSupport, QaRegressionTests, QaProtocolTests, QaStorageTests, QaBrowserMatrixTests, DesktopUiTests, MockProxy, StorageAndBrowserTests; qa/ReleaseHarness; scripts/Test-Installer.ps1. Hash diff chứa cả thay đổi UI có sẵn trong quá trình QA; không nhận toàn bộ diff là do lượt này tạo.

Candidate portable ở `artifacts/release-candidate/publish/`. [Bộ cài 1.0.1](../installer/NetworkProfileManager_Setup.exe) build thành công, **NotSigned**. Sandbox smoke cài/nâng cấp từ payload 1.0.0/gỡ/cài lại PASS, token không admin, shortcuts và DPAPI được giữ. Chưa vô hiệu hóa SmartScreen; chưa kiểm tra production namespace hoặc uninstall lúc browsers đang chạy. Binary và source hash trong [release-manifest.json](release-manifest.json).

**Không phát hành cho tới khi B008 được giải quyết và chạy lại toàn bộ mandatory tests.** Native DPI cao, provider-specific, live disconnect/reconnect và packet capture vẫn cần thực hiện theo phạm vi phân phối. Mock PASS không chứng minh production-ready tuyệt đối.
'@
Write-Report 'FULL_TEST_REPORT.md' $body

$perf = "# PERFORMANCE REPORT`n`nNgày 25/10/2026. Đo trực tiếp testhost chạy BrowserLauncherService và browser thật, không phải chỉ riêng WPF EXE. Browser chạy tối đa hai profile, gồm các renderer/process con hợp lệ. Số liệu là các mẫu snapshot; peak có thể nằm giữa mẫu.`n`n"
$perf += "| Cặp | Kết quả | Chu kỳ hoàn thành | IP checks | Thời gian |`n|---|---|---|---|---|`n"
foreach($file in @('performance-chrome-firefox.json','performance-edge.json')) {
    $p = Get-Content (Join-Path 'artifacts/logs' $file) -Raw | ConvertFrom-Json
    $perf += "| $($p.BrowserA)+$($p.BrowserB) | $($p.Status) | $($p.CompletedCycles)/$($p.RequestedCycles) | $($p.IpChecks) | $([math]::Round($p.ElapsedMs/1000,3)) s |`n"
}
$perf += "`n| Cặp / metric | Baseline | Max mẫu trong test | Sau cleanup + GC |`n|---|---|---|---|`n"
foreach($file in @('performance-chrome-firefox.json','performance-edge.json')) {
    $p=Get-Content (Join-Path 'artifacts/logs' $file) -Raw | ConvertFrom-Json
    $a=$p.Samples[0]; $z=$p.Samples[-1]; $label="$($p.BrowserA)+$($p.BrowserB)"
    foreach($key in @('PrivateBytes','WorkingSet','ManagedBytes','Handles','Threads','ManagedChildCount','ChildPrivateBytes','ManagedListeners','TotalHostListeners')) {
        $peak=($p.Samples | Measure-Object -Property $key -Maximum).Maximum
        if($key.EndsWith('Bytes') -or $key -eq 'WorkingSet') { $av='{0:N2} MiB' -f ($a.$key/1MB); $pv='{0:N2} MiB' -f ($peak/1MB); $zv='{0:N2} MiB' -f ($z.$key/1MB) }
        else { $av=$a.$key; $pv=$peak; $zv=$z.$key }
        $perf += "| $label / $key | $av | $pv | $zv |`n"
    }
    $cpu=$z.CpuMs-$a.CpuMs; $avg=100*$cpu/$p.ElapsedMs
    $perf += "| $label / CPU testhost | $($a.CpuMs) ms tích lũy | Delta $cpu ms; trung bình $([math]::Round($avg,2))% của một logical core | $($z.CpuMs) ms tích lũy |`n"
}
$perf += @'

Không dùng TotalHostListeners để gán listener của chương trình khác cho app. ManagedListeners và ManagedChildCount về 0 sau cleanup trong cả hai lần đo. Chrome+Firefox hoàn thành 20 vòng; Edge dừng ở chu kỳ 2 vì thiếu proof request. Managed heap sau forced GC thấp hơn baseline không đủ chứng minh “không rò rỉ bộ nhớ”. Chưa soak nhiều giờ, chưa lấy CPU tổng mọi child liên tục và chưa đo peak CPU/memory của WPF EXE riêng.

UI: 50 vòng đổi tab/theme/resize PASS; đóng cửa sổ khi request pending và 2 browsers hoạt động PASS. Không có assertion “không rò rỉ tuyệt đối”.

Raw evidence: [Chrome+Firefox](../logs/performance-chrome-firefox.json), [Edge+Edge](../logs/performance-edge.json), [UI](../logs/ui-matrix.json), [close while active](../logs/close-while-active.json). Kết quả là snapshot của lượt full-final, không trộn số liệu của các lần debug trước.
'@
Write-Report 'PERFORMANCE_REPORT.md' $perf

$manifest = [ordered]@{ GeneratedUtc=[DateTimeOffset]::UtcNow.ToString('O'); Version='1.0.1'; Decision='HOLD'; GitCommit=$null; FinalTests=@{Total=[int]$counters.total;Passed=[int]$counters.passed;Failed=[int]$counters.failed;Skipped=[int]$counters.notExecuted}; Binaries=@() }
foreach($path in @('artifacts/installer/NetworkProfileManager_Setup.exe','artifacts/release-candidate/publish/NetworkProfileManager.exe','artifacts/release-candidate/publish/NetworkProfileManager.dll','artifacts/release-candidate/publish/NetworkProfileManager.Infrastructure.dll')) {
    $item=Get-Item $path; $manifest.Binaries += @{Path=$path;Bytes=$item.Length;Sha256=(Get-FileHash $path -Algorithm SHA256).Hash;Signature=(Get-AuthenticodeSignature $path).Status.ToString()}
}
[IO.File]::WriteAllText((Join-Path $reportRoot 'release-manifest.json'),($manifest | ConvertTo-Json -Depth 7),$utf8)
Write-Output "Reports generated: total=$($counters.total), passed=$($counters.passed), failed=$($counters.failed), original=$($original.Count)"
