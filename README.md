Hướng dẫn sử dụng code điều khiển robot hai bánh bằng tay cầm PS5 với giao thức CAN (PDO mode) được cung cấp ở trên:

---

### **Tổng quan**
Code này được viết bằng C# để điều khiển một robot hai bánh sử dụng giao thức CAN (Controller Area Network) với chế độ PDO (Process Data Object) để tối ưu hóa tốc độ truyền dữ liệu. Robot được điều khiển bằng tay cầm PS5 thông qua giao diện USB. Các thành phần chính bao gồm:

1. **PS5 Controller**: Đọc dữ liệu từ tay cầm PS5 (joystick, nút bấm) để điều khiển robot.
2. **UbuntuCANInterface**: Giao tiếp với bus CAN để gửi/nhận dữ liệu từ các động cơ.
3. **CiA402Motor**: Điều khiển động cơ theo chuẩn CiA402 (thường dùng cho động cơ servo).
4. **TwoWheelRobot**: Điều phối hai động cơ để di chuyển robot (tiến, lùi, quay).

---

### **Yêu cầu phần cứng**
- **Tay cầm PS5**: Kết nối qua USB với máy tính chạy Ubuntu.
- **Giao diện CAN**: Thiết bị CAN như USB-to-CAN adapter (ví dụ: PEAK-System, Kvaser) được kết nối với bus CAN.
- **Hai động cơ**: Hỗ trợ chuẩn CiA402, kết nối với bus CAN (mỗi động cơ có Node ID riêng, mặc định là 1 và 2).
- **Máy tính chạy Ubuntu**: Đã cài đặt môi trường .NET và các công cụ CAN.

---

### **Yêu cầu phần mềm**
1. **Hệ điều hành**: Ubuntu (đã thử nghiệm trên Ubuntu 20.04 hoặc mới hơn).
2. **.NET SDK**: Phiên bản tương thích với code (ví dụ: .NET 6 hoặc 8).
3. **Thư viện HidSharp**: Để giao tiếp với tay cầm PS5 qua USB.
   - Cài đặt: `dotnet add package HidSharp`
4. **can-utils**: Công cụ dòng lệnh để quản lý CAN interface.
   - Cài đặt: `sudo apt update && sudo apt install can-utils`
5. **Quyền root**: Một số lệnh CAN yêu cầu quyền `sudo`.

---

### **Cài đặt và cấu hình**
1. **Kiểm tra giao diện CAN**:
   - Kết nối thiết bị CAN (USB-to-CAN adapter) vào máy tính.
   - Chạy lệnh để kiểm tra:
     ```bash
     ip link show
     ```
     - Kết quả nên hiển thị giao diện CAN (ví dụ: `can0`).
   - Nếu không thấy, đảm bảo đã load module CAN:
     ```bash
     sudo modprobe can
     sudo modprobe can_raw
     ```

2. **Thiết lập giao diện CAN**:
   - Thiết lập bitrate (mặc định là 500000 trong code):
     ```bash
     sudo ip link set can0 type can bitrate 500000
     sudo ip link set can0 up
     ```
   - Kiểm tra trạng thái:
     ```bash
     ip -details link show can0
     ```
     - Nếu giao diện ở trạng thái `UP`, bạn đã sẵn sàng.

3. **Kết nối tay cầm PS5**:
   - Cắm tay cầm PS5 qua cổng USB.
   - Kiểm tra thiết bị:
     ```bash
     lsusb | grep 054c:0ce6
     ```
     - Nếu không thấy, thử cắm lại hoặc kiểm tra cáp USB.
   - Nếu gặp lỗi quyền truy cập, thêm udev rule:
     ```bash
     sudo nano /etc/udev/rules.d/99-ps5-controller.rules
     ```
     - Thêm dòng:
       ```
       SUBSYSTEM=="hidraw", ATTRS{idVendor}=="054c", ATTRS{idProduct}=="0ce6", MODE="0666"
       ```
     - Reload udev:
       ```bash
       sudo udevadm control --reload-rules
       sudo udevadm trigger
       ```

4. **Cài đặt dự án .NET**:
   - Tạo thư mục dự án và sao chép code vào file `Program.cs`.
   - Cài đặt HidSharp:
     ```bash
     dotnet add package HidSharp
     ```
   - Biên dịch và chạy:
     ```bash
     dotnet build
     sudo dotnet run
     ```

---

### **Cách sử dụng**
1. **Khởi động chương trình**:
   - Chạy lệnh:
     ```bash
     sudo dotnet run
     ```
   - Chương trình sẽ:
     - Kết nối với giao diện CAN (`can0`, bitrate 500000).
     - Khởi tạo hai động cơ (Node ID 1 và 2).
     - Cấu hình PDO để truyền dữ liệu thời gian thực.
     - Kết nối với tay cầm PS5.
     - Bắt đầu điều khiển robot.

2. **Điều khiển robot**:
   - **R1**: Phải giữ nút R1 để kích hoạt điều khiển (tính năng an toàn).
   - **Joystick trái (trục Y)**: Điều khiển tiến/lùi.
     - Đẩy lên: Tiến.
     - Kéo xuống: Lùi.
   - **Joystick phải (trục X)**: Điều khiển quay tại chỗ.
     - Sang phải: Quay phải.
     - Sang trái: Quay trái.
   - **Nút PS**: Nhấn để dừng robot và thoát chương trình.
   - **Deadzone**: Joystick có vùng chết (35/255) để tránh nhiễu nhỏ.

3. **Theo dõi trạng thái**:
   - Console sẽ hiển thị trạng thái:
     - **Target**: Tốc độ mục tiêu (RPM) của động cơ trái/phải.
     - **Actual**: Tốc độ thực tế (RPM) từ dữ liệu TPDO2.
     - **PDO Debug**: Mỗi 100 frame PDO, chương trình in thông tin chi tiết về trạng thái động cơ (StatusWord, vị trí, tốc độ).
   - Mỗi 5 giây, chương trình in thông tin chi tiết về trạng thái động cơ (StatusWord, vị trí).

4. **Dừng chương trình**:
   - Nhấn nút **PS** trên tay cầm để dừng robot, tắt động cơ và thoát an toàn.
   - Hoặc dùng `Ctrl+C` trên terminal (nhưng nên dùng nút PS để đảm bảo động cơ được tắt đúng cách).

---

### **Cấu hình nâng cao**
1. **Thay đổi giao diện CAN**:
   - Mặc định: `can0`.
   - Sửa trong `Main`:
     ```csharp
     string canInterface = "can1"; // Thay can0 bằng can1 hoặc tên giao diện khác
     ```

2. **Thay đổi Node ID**:
   - Mặc định: Động cơ trái (Node 1), động cơ phải (Node 2).
   - Sửa trong `Main`:
     ```csharp
     byte leftNodeId = 3; // Node ID mới cho động cơ trái
     byte rightNodeId = 4; // Node ID mới cho động cơ phải
     ```

3. **Điều chỉnh tốc độ tối đa**:
   - Mặc định: 1500 RPM.
   - Sửa trong `TwoWheelRobot`:
     ```csharp
     private const double MAX_RPM = 2000.0; // Tăng tốc độ tối đa
     ```

4. **Điều chỉnh deadzone**:
   - Mặc định: 35/255.
   - Sửa trong `TwoWheelRobot`:
     ```csharp
     private const double DEADZONE = 50.0; // Tăng vùng chết
     ```

5. **Tần suất cập nhật**:
   - Mặc định: Cập nhật mỗi 20ms.
   - Sửa trong `TwoWheelRobot`:
     ```csharp
     private const int UPDATE_INTERVAL_MS = 10; // Giảm xuống 10ms cho phản hồi nhanh hơn
     ```

---

### **Khắc phục sự cố**
1. **Không tìm thấy tay cầm PS5**:
   - Kiểm tra kết nối USB: `lsusb | grep 054c:0ce6`.
   - Thêm udev rule như hướng dẫn ở trên.
   - Chạy với `sudo`:
     ```bash
     sudo dotnet run
     ```

2. **Không kết nối được CAN interface**:
   - Kiểm tra giao diện: `ip link show`.
   - Đảm bảo can-utils được cài: `sudo apt install can-utils`.
   - Kiểm tra module CAN:
     ```bash
     sudo modprobe can
     sudo modprobe can_raw
     ```
   - Thử thiết lập lại:
     ```bash
     sudo ip link set can0 down
     sudo ip link set can0 type can bitrate 500000
     sudo ip link set can0 up
     ```

3. **Động cơ không phản hồi**:
   - Kiểm tra Node ID của động cơ có khớp với cấu hình (1 và 2).
   - Kiểm tra trạng thái động cơ qua console (StatusWord từ TPDO1).
   - Nếu động cơ ở trạng thái Fault (0x08), chương trình sẽ tự reset. Nếu không khắc phục được, kiểm tra kết nối vật lý hoặc cấu hình PDO.

4. **Hiệu suất PDO kém**:
   - Đảm bảo bitrate CAN phù hợp với động cơ (mặc định 500000).
   - Kiểm tra độ trễ bus CAN: `candump can0`.
   - Giảm tần suất cập nhật nếu cần (tăng `UPDATE_INTERVAL_MS`).

---

### **Lưu ý an toàn**
- **Giữ nút R1**: Robot chỉ di chuyển khi giữ R1, tránh tai nạn do vô tình chạm joystick.
- **Nút PS thoát an toàn**: Luôn dùng nút PS để dừng robot, đảm bảo động cơ được tắt đúng cách.
- **Kiểm tra động cơ**: Trước khi chạy, đảm bảo động cơ ở trạng thái `OperationEnabled` (StatusWord = 0x27).
- **Quyền root**: Một số lệnh CAN yêu cầu `sudo`. Đảm bảo chạy với quyền phù hợp.

---

### **Mở rộng**
- **Thêm cảm biến**: Kết hợp dữ liệu từ cảm biến (qua CAN hoặc giao diện khác) để tự động hóa.
- **Điều khiển PID**: Thêm vòng điều khiển PID dựa trên tốc độ thực tế (`ActualVelocity`) từ TPDO2.
- **Giao diện người dùng**: Tạo GUI để hiển thị trạng thái robot và điều khiển trực quan hơn.
- **Ghi log**: Lưu dữ liệu PDO vào file để phân tích hiệu suất.

---

### **Ví dụ kết quả console**
Khi chạy chương trình thành công, bạn sẽ thấy:
```
====================================
  PS5 Two-Wheel Robot with PDO     
====================================

Cau hinh:
   CAN Interface: can0
   Baudrate: 500000
   Left Motor: Node 1
   Right Motor: Node 2
   Mode: PDO (Real-time control)

Thiet lap CAN interface...
[OK] CAN interface da san sang!

Khoi dong NMT...
Khoi tao Motor Trai (Node 1):
  ✓ PDO đã được cấu hình cho Node 1!
  ✓ Motor Node 1 sẵn sàng!

Khoi tao Motor Phai (Node 2):
  ✓ PDO đã được cấu hình cho Node 2!
  ✓ Motor Node 2 sẵn sàng!

Ket noi PS5 Controller...
[OK] Da tim thay: Wireless Controller
[OK] Ket noi thanh cong voi PS5 Controller!

[OK] He thong da san sang!
Dieu khien:
   R1: GIU de kich hoat dieu khien
   Joystick TRAI Y:  Tien/Lui
   Joystick PHAI X:  Quay tai cho/Re
   PS button: Thoat

[STATUS] Target: L=0.0 R=0.0 | Actual: L=   0.0 | R=   0.0
[PDO] Node 1 TPDO1: Status=0x0027 Pos=123456
[PDO] Node 1 TPDO2: Vel=0 (0.0 RPM) Mode=9
```

---

Nếu cần thêm chi tiết hoặc hỗ trợ cụ thể (ví dụ: debug lỗi, thêm tính năng), hãy cho tôi biết!
