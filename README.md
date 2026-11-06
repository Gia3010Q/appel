# Eldorado Desk

Ứng dụng Windows 10/11 x64 bằng C#/.NET 8 và WPF, giao diện tiếng Việt cho một shop Eldorado. Mã nguồn triển khai theo đặc tả OpenAPI do chủ shop cung cấp.

## Chức năng

- Đọc 50 đơn mới nhất, lọc đơn chờ giao, xem hạn giao khi API có dữ liệu và xác nhận giao sau bước kiểm tra mã đơn.
- Đọc yêu cầu cày thuê đang hoạt động, nhập giá, tiền tệ và thời gian hoàn thành rồi gửi báo giá cho đúng mã yêu cầu. Lưu bản nháp bằng SQLite.
- Đọc các nhóm offer tài khoản, predefined, item và currency. Sửa giá hoặc số lượng theo khả năng của từng nhóm.
- Chạy ở khay hệ thống, đồng bộ mỗi 60 giây, giãn khoảng gọi khi gặp giới hạn API. Lưu mốc đã xem để tránh báo lại dữ liệu cũ sau khi mở app.
- Lưu credentials trong Windows Credential Manager. Access token chỉ giữ trong bộ nhớ.

## Build trên cloud

1. Đưa mã nguồn vào repository GitHub của bạn, giữ nguyên thư mục `.github/workflows`.
2. Mở **Actions → Build Windows app → Run workflow**. Workflow cũng chạy khi push lên `main` hoặc `master`, khi mở pull request và khi push tag `v*`.
3. Workflow chạy kiểm thử hồi quy bằng HTTP mô phỏng, publish bản Windows x64 tự chứa .NET và tạo bộ cài bằng Inno Setup.
4. Khi workflow thành công, tải artifact **EldoradoDesk-Windows-x64**. Giải nén và chạy `EldoradoDesk-Setup.exe`. Artifact còn có `EldoradoDesk-portable-win-x64.zip` để chạy trực tiếp.

Build này dùng máy Windows của GitHub Actions. Máy sử dụng app không cần cài .NET SDK hoặc .NET Runtime. Workflow không cần credentials Eldorado hoặc dữ liệu shop để build và kiểm thử.

## Kết nối shop

Trong Eldorado Dashboard → Seller API, tạo credentials cho app rồi nhập Client ID và Client Secret vào phần kết nối trong app. Secret chỉ hiện một lần trên Eldorado. Khi kết nối lại bằng tài khoản khác, kiểm tra đúng shop trước khi thực hiện thao tác.

Ở mục **Cày thuê**, chọn yêu cầu, nhập giá và thời gian giao, lưu nháp hoặc kiểm tra rồi gửi báo giá. App chỉ xác nhận đã gửi khi phản hồi có mã offer và mã yêu cầu khớp nhau. Kết quả mất kết nối trong lúc gửi được giữ ở trạng thái cần kiểm tra. Mở Eldorado kiểm tra offer trước khi cho phép gửi lại. Bản nháp trên máy và báo giá đã được Eldorado nhận có trạng thái riêng.

Thu nhỏ hoặc đóng cửa sổ để app tiếp tục chạy ở khay hệ thống. Chọn **Thoát** trong menu khay để dừng hoàn toàn. Thông báo mở lại mục tương ứng trong app. Các thao tác chat mở website Eldorado.

## Kiểm thử và giới hạn

Chạy bộ kiểm thử trên cloud qua workflow. Nếu phát triển trên máy có SDK, dùng `dotnet run --project tests/Eldorado.Tests/Eldorado.Tests.csproj` và `dotnet build src/Eldorado.Desktop/Eldorado.Desktop.csproj` trên Windows.

Bộ kiểm thử dùng HTTP giả lập để kiểm tra token hết hạn, làm mới token cho lệnh đọc, nội dung báo giá, yêu cầu đã đóng, phản hồi mất hoặc sai mã yêu cầu, hai lần gửi đồng thời, giới hạn lượt gọi và lưu SQLite qua lần khởi động lại. Kiểm thử tránh gửi thao tác tới shop thật.

- Danh sách đơn và yêu cầu hiển thị 50 mục mới nhất. Đặc tả có cursor nhưng không mô tả cách tách cursor thành tham số cho trang tiếp theo. Khoảng app nghỉ lâu có hơn 50 mục mới cần kiểm tra thêm trên website.
- Danh sách yêu cầu API chỉ cung cấp game ID, loại dịch vụ, người mua và ngày tạo. Nội dung chi tiết cần xem trên Eldorado trước khi đặt giá.
- Currency offer có một phần response là schema mở. Khi dữ liệu thực tế không có giá theo cấu trúc đã biết, app báo lỗi để người dùng kiểm tra trên website.
- Chưa có kết quả xác minh trực tiếp trên shop thật. Eldorado có môi trường production, nên lần sửa giá, giao đơn hoặc gửi báo giá đầu tiên cần dùng một mục bạn đã chủ động chọn.
- Bộ cài được tạo khi cloud workflow thành công. Mã nguồn và cấu hình workflow tự chúng chưa phải kết quả build đã kiểm chứng.

## Tài liệu nguồn

- [Cách Seller API hoạt động](https://support.eldorado.gg/en/articles/16127288-how-the-seller-api-works)
- [Swagger Seller API](https://www.eldorado.gg/swagger/seller/index.html)
- [Bán dịch vụ cày thuê](https://support.eldorado.gg/en/articles/8408999-sell-boosting-services)
