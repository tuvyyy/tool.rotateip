# Cảnh báo host-resolver-rules — 26/10/2026

Giữ nguyên tham số `--host-resolver-rules=MAP * ~NOTFOUND, EXCLUDE 127.0.0.1`. Chưa có phương án trong phạm vi khởi chạy browser vừa bỏ cảnh báo vừa giữ được lớp chặn DNS tương đương. Không sửa BrowserLauncher/BrowserAdapters hoặc UI, không tắt cảnh báo bảo mật.

## Nguyên nhân và đối chiếu Chromium

Tham số nằm trong `ChromiumBrowserAdapter.Prepare`, được dùng chung cho Chrome, Edge và Cốc Cốc. Browser luôn kết nối tới HTTP bridge `127.0.0.1`, kể cả khi upstream của app là SOCKS5.

Chromium vẫn định nghĩa và xử lý `host-resolver-rules`. Nó cũng chủ động đưa cả `host-resolver-rules` lẫn alias `host-rules` vào danh sách tham số gây cảnh báo vì chúng có thể chuyển hướng phân giải tên miền. Vì vậy cảnh báo không có nghĩa tham số đã ngừng hoạt động. Đổi sang alias không phải giải pháp: alias cũng bị cảnh báo và đã được đánh dấu deprecated. Nguồn: [bad_flags_prompt.cc](https://chromium.googlesource.com/chromium/src/+/HEAD/chrome/browser/ui/startup/bad_flags_prompt.cc), [network_switches.cc](https://chromium.googlesource.com/chromium/src/+/HEAD/services/network/public/cpp/network_switches.cc).

`MAP * ~NOTFOUND` làm các lookup tên miền qua mapped resolver thất bại; `EXCLUDE 127.0.0.1` dành ngoại lệ cho địa chỉ bridge. Mã hiện tại trả `ERR_NAME_NOT_RESOLVED` cho phép ánh xạ không hợp lệ trước khi giao công việc cho resolver bên dưới. Đây là thay đổi có chủ ý ở resolver của Chrome, không phải thay DNS/MAC/IP của Windows. Nguồn: [mapped_host_resolver.cc](https://chromium.googlesource.com/chromium/src/+/HEAD/net/dns/mapped_host_resolver.cc).

Theo [tài liệu proxy Chromium hiện tại](https://chromium.googlesource.com/chromium/src/+/HEAD/net/docs/proxy.md), HTTP proxy nhận tên miền đích và chịu trách nhiệm phân giải; HTTPS dùng CONNECT. Với proxy cố định không có `direct://`, lỗi kết nối proxy làm request thất bại. Vì thế tham số resolver **không cần thiết để DNS của request HTTP/HTTPS thông thường đi qua bridge**.

Tuy nhiên, proxy của URL không bao phủ mọi lời gọi DNS trực tiếp của trình duyệt. Tài liệu [SOCKS proxy của Chromium](https://www.chromium.org/developers/design-documents/network-stack/socks-proxy/) giải thích lý do dùng resolver rule làm lớp chặn rộng hơn việc chỉ tắt DNS prefetch. Trang này có một số phần lịch sử; kết luận trong báo cáo còn được đối chiếu với mã Chromium hiện tại và kiểm thử Chrome cài trên máy.

## Phép thử Chrome thật

Chrome trên máy: **155.0.8059.86**. Các tiến trình test dùng headless, profile tạm riêng, gateway fixture có xác thực và local bridge của app. Không đụng profile browser của người dùng.

Để kiểm tra resolver ngoài luồng URL, test chủ động gọi công cụ DNS của `chrome://net-internals` cho `example.com`; callback gọi trực tiếp `NetworkContext::ResolveHost` theo [mã net_internals_ui.cc](https://chromium.googlesource.com/chromium/src/+/HEAD/chrome/browser/ui/webui/net_internals/net_internals_ui.cc). Đây là canary chủ động, không phải bằng chứng một trang web thông thường đã làm rò DNS.

| Cấu hình Chrome với cùng HTTP bridge | Kết quả lookup chủ động | Event resolver cục bộ trong NetLog |
| --- | --- | --- |
| Giữ resolver rule | `ERR_NAME_NOT_RESOLVED` | 0 |
| Bỏ resolver rule | Phân giải thành công | Có |
| Bỏ rule, đặt `net.network_prediction_options=2` | Phân giải thành công | Có |

Giá trị 2 tắt network prediction theo [preloading_prefs.h](https://chromium.googlesource.com/chromium/src/+/HEAD/chrome/browser/preloading/preloading_prefs.h). Phép thử chứng minh lựa chọn này không thay thế được lớp chặn lookup trực tiếp. Số event NetLog không được diễn giải thành số DNS packet hoặc số truy vấn trên dây.

Các test khác:

- Chrome đọc được IP fixture `203.0.113.7` qua HTTPS trên cả HTTP upstream và SOCKS5 upstream. Chạy đối chiếu có/không có resolver rule; cả bốn cấu hình giữ tên miền `echo.test` ở phía upstream, không có event resolver cục bộ cho tên miền đích.
- Trang HTTPS có `isSecureContext=true`. Chứng chỉ tự ký không được tin cậy vẫn bị chặn khi dùng cấu hình khởi chạy production.
- Test HTTPS dương chỉ tin public key của chứng chỉ fixture ngẫu nhiên bằng SPKI pin trong tiến trình test. Không cài CA, không thêm ngoại lệ chứng chỉ vào cấu hình production.
- HTTP đi qua proxy ngay cả khi đích là loopback: direct trap không nhận kết nối. Điều này kiểm tra `--proxy-bypass-list=<-loopback>` đang có hiệu lực; ngoại lệ resolver cho 127.0.0.1 không có nghĩa bypass proxy.
- Khi upstream chủ động đóng kết nối, bridge báo lỗi và direct trap vẫn nhận 0 kết nối, với cả HTTP/SOCKS5 upstream.
- Khi đóng listener bridge trong lúc Chrome vẫn chạy, điều hướng mới trả `ERR_PROXY_CONNECTION_FAILED`; direct trap vẫn nhận 0 kết nối.
- Ba regression case cho Chrome/Edge/Cốc Cốc giữ một proxy cố định, resolver guard và TLS; cấm các tham số làm ẩn/thay cảnh báo hoặc tắt sandbox/kiểm tra chứng chỉ trong launch arguments production.

## Kết quả và phạm vi

- Thêm **13 regression case** tại `tests/NetworkProfileManager.Tests/ChromiumDnsRoutingTests.cs`.
- **73/73 test liên quan PASS**, gồm Chrome thật, protocol HTTP/SOCKS5, forwarding, CONNECT và kiểm tra lifecycle launcher. Kết quả: `artifacts/tests/chromium-browser-related.trx`.
- Build Release cuối thành công: **0 warning, 0 error**. Một lần build trước đó bị khóa DLL test do testhost còn chạy; chạy lại sau khi testhost kết thúc đã thành công.
- Bằng chứng rút gọn: `artifacts/logs/chromium-*.json`. NetLog thô chỉ được tạo trong profile tạm và dọn sau test; không thu credentials, lịch sử hoặc NetLog của profile thật.
- IP là IP echo fixture để kiểm chứng tuyến browser → bridge → upstream. Không đo public exit IP hoặc DNS resolver của dịch vụ Home Proxy thương mại trong lượt này; NetLog cũng không phải phép bắt mọi packet của Windows.
- DNS để app tìm host upstream vẫn thuộc app/.NET; tham số của Chrome không chặn DNS này. Không suy rộng các test thành bảo đảm chặn mọi lưu lượng của extension, mọi cơ chế ngoài resolver, hoặc mọi tiến trình Windows.
- Không chạy stress suite, không thay MAC/IP/DNS máy, không gọi API xoay IP. Không thay đổi binary production, shortcut Desktop hoặc phiên app đang mở, vì không có sửa đổi runtime cần phát hành.

Giữ cảnh báo là quyết định bảo toàn hành vi DNS hiện tại. Tắt prefetch, tắt DoH hoặc đổi alias không cung cấp bằng chứng về một lớp chặn tương đương. Mọi phương án bỏ resolver guard về sau phải kiểm chứng cả các lookup ngoài URL, thay vì chỉ xác nhận trang web vẫn mở được.

Lệnh chạy lại tập test liên quan:

```powershell
dotnet test tests/NetworkProfileManager.Tests -c Release --filter "(FullyQualifiedName~ChromiumDnsRoutingTests|FullyQualifiedName~ProxyIntegrationTests|FullyQualifiedName~QaProtocolTests|FullyQualifiedName~StorageAndBrowserTests.BrowserLauncher|FullyQualifiedName~StorageAndBrowserTests.BrowserProfilesHaveDistinctPathsAndNoSecretsInCommand)&Category!=Stress" --logger "trx;LogFileName=chromium-browser-related.trx" --results-directory artifacts/tests
```
