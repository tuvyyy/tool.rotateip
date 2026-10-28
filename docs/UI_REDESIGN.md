# Thiết kế lại giao diện Windows 11 (Full UI/UX Redesign)

Tài liệu này tóm tắt kiến trúc giao diện mới của **Network Profile Manager V1** sau đợt kiểm toán (Audit) và thiết kế lại toàn diện theo chuẩn Windows 11 Desktop.

## 1. Hệ thống Thiết kế (Design System)

Toàn bộ token màu sắc, chữ viết, khoảng cách và control template được quản lý tập trung tại `src/NetworkProfileManager.App/App.xaml` và `src/NetworkProfileManager.App/Services/DesktopService.cs`:

- **Typography (`Segoe UI Variable Text` / `Segoe UI`)**:
  - `PageTitleText`: `22px` Bold (`#0F172A` Light / `#F8FAFC` Dark)
  - `SectionTitleText`: `16px` SemiBold
  - `CardTitleText`: `15px` SemiBold
  - `BodyText`: `13px` Regular
  - `BodyStrongText`: `13px` SemiBold
  - `CaptionText`: `12px` Regular (`SecondaryTextBrush`)
  - `MetricIpText`: `19px` Bold (`AccentBrush`)
- **Spacing Scale**: `4 / 8 / 12 / 16 / 20 / 24 / 32 DIP`.
- **Uniform Control Sizing**:
  - `Button`, `TextBox`, `PasswordBox`, `ComboBox`, `DatePicker`: Chiều cao chuẩn `38 DIP`, bo góc `6 DIP`.
  - `CardBorder`: Bo góc `10 DIP`, viền `1px`, `Padding="20"`.
  - `HelpIcon`: Nút trợ giúp ngữ cảnh `?` (`18×18 DIP`, vùng nhấn cảm ứng `24×24 DIP`) hiển thị `ToolTip` tiếng Việt tức thời (`InitialShowDelay="80"`).

## 2. Cấu trúc Cửa sổ chính (Shell Architecture)

- **Full-height Flush Sidebar (`220 DIP`)**:
  - Gắn liền vào cạnh trái, cạnh trên và cạnh dưới của cửa sổ (`Margin="0"`, `CornerRadius="0"`, `BorderThickness="0,0,1,0"`).
  - Có thanh chỉ báo trang đang chọn (`3×20 DIP`, `AccentBrush`) và trạng thái Hover/Selected tách biệt.
- **Contextual Header Bar**:
  - Hiển thị tiêu đề trang, mô tả ngắn và **chỉ các nút hành động thuộc ngữ cảnh của trang đang mở**.
  - Nút `Hủy tác vụ` chỉ xuất hiện khi `IsBusy == true`.
- **Responsive Content Region (`ResponsiveColumnsPanel`)**:
  - Bố cục tự động hiển thị 2 cột cân đối trên màn hình rộng và chuyển thành 1 cột dọc có thanh cuộn khi chiều rộng vùng nội dung nhỏ hơn `780 DIP` (tương ứng cửa sổ `< 1060 DIP`).

## 3. Tổ chức 7 Trang chức năng

1. **Tổng quan (Dashboard)**: Banner trạng thái Proxy Gateway + 2 thẻ tóm tắt `DashboardSummaryCard` cho Trình duyệt A & B (không lặp lại form cấu hình chi tiết của trang Hai trình duyệt).
2. **Cấu hình proxy**: Bố cục 2 cột gồm thẻ Form cấu hình chia 3 nhóm (`1. Máy chủ kết nối`, `2. Thông tin xác thực` có nút **Hiện/Ẩn mật khẩu**, `3. Vòng đời & Chế độ xoay IP` với `DatePicker` tiếng Việt) và thẻ **Trạng thái Gateway**.
3. **Hai trình duyệt**: Hai thẻ cấu hình chi tiết cho Profile A & B có chiều cao bằng nhau, bổ sung nút `Tự phát hiện` đường dẫn `.exe` và hàng 3 nút đồng kích thước (`UniformGrid Columns="3"`).
4. **Kiểm tra IP**: Thanh công cụ kiểm tra + bộ lọc theo tuyến (`Tất cả tuyến`, `A`, `B`, `Gateway`, `Direct`) + `DataGrid` tỷ lệ chuẩn kèm **Empty State** (`"Chưa có kết quả kiểm tra"`).
5. **Thông tin mạng**: Chia thành 4 thẻ chuyên biệt (`Wi-Fi & Bộ điều hợp mạng`, `Mạng nội bộ`, `Địa chỉ IP công khai & DNS` hỗ trợ tự xuống dòng + tooltip, `Công cụ mạng & Quản trị nâng cao` tách riêng `Flush DNS` / `DHCP Renew` có xác nhận).
6. **Nhật ký**: Thanh lọc mức độ (`Info`/`Warning`/`Error`), ô tìm kiếm từ khóa, nút `Xóa lọc`, `Tải lại`, `Xuất CSV` và **Empty State** (`"Chưa có nhật ký hiển thị"`).
7. **Cài đặt**: Chia thành 4 nhóm thẻ cân đối (`Giao diện hiển thị`, `Kiểm tra kết nối`, `Dữ liệu & Quyền riêng tư`, `Thông tin ứng dụng`).

## 4. Báo cáo & Ảnh chụp Kiểm định

- Báo cáo Audit: `artifacts/reports/UI_AUDIT.md`
- Nhật ký thay đổi: `artifacts/reports/UI_CHANGELOG.md`
- Báo cáo Visual QA: `artifacts/reports/UI_VISUAL_QA.md`
- Ảnh chụp trước khi sửa: `artifacts/screenshots/before/`
- Ảnh chụp sau khi sửa (Light & Dark, Responsive, DPI): `artifacts/screenshots/after/light/` và `artifacts/screenshots/after/dark/`
