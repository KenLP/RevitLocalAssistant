# VI ↔ EN BIM Glossary

> Source of truth cho bộ từ điển Việt–Anh chuyên ngành Revit/BIM.
> File này được compile thành `BimGlossary.cs` trong Phase 5.
> Thêm entries theo format dưới đây — mỗi entry phải verified với Revit BuiltInCategory thực tế.

---

## Categories (Danh mục cấu kiện)

| Tiếng Việt (VI) | English (EN) | BuiltInCategory | Ghi chú |
|---|---|---|---|
| tường | wall / walls | `OST_Walls` | |
| tường ngăn cháy | fire-rated wall | `OST_Walls` | scope: FireRating param |
| sàn | floor | `OST_Floors` | |
| trần | ceiling | `OST_Ceilings` | |
| mái | roof | `OST_Roofs` | |
| cột | column | `OST_Columns` | Architectural |
| cột kết cấu | structural column | `OST_StructuralColumns` | |
| dầm / xà | beam / framing | `OST_StructuralFraming` | |
| cửa đi | door | `OST_Doors` | |
| cửa sổ | window | `OST_Windows` | |
| cửa thoát hiểm | exit door | `OST_Doors` | scope: fire exit comment |
| phòng | room | `OST_Rooms` | |
| không gian | space | `OST_MEPSpaces` | |
| tầng / cao độ | level | `OST_Levels` | |
| lưới trục / trục | grid | `OST_Grids` | |
| ống nước | pipe | `OST_PipeCurves` | |
| ống gió / duct | duct | `OST_DuctCurves` | |
| thiết bị cơ | mechanical equipment | `OST_MechanicalEquipment` | |
| thiết bị điện | electrical equipment | `OST_ElectricalEquipment` | |
| đèn | lighting fixture | `OST_LightingFixtures` | |
| thang bộ | stair | `OST_Stairs` | |
| cầu thang | stair | `OST_Stairs` | |
| lan can | railing | `OST_Railings` | |
| vật liệu | material | `OST_Materials` | |
| lỗ mở / opening | opening | `OST_ShaftOpening` | |

---

## Parameters (Tham số phổ biến)

| Tiếng Việt (VI) | English (EN) | Revit Parameter Name | StorageType | Ghi chú |
|---|---|---|---|---|
| mã hiệu | mark | `Mark` | String | Instance |
| tên | name | `Name` | String | Instance |
| chú thích / bình luận | comments | `Comments` | String | Instance |
| cấp chống cháy | fire rating | `Fire Rating` | String | |
| chiều cao | height | `Height` | Double | meters |
| chiều cao thông thủy | clear height | `Clear Height` | Double | |
| bề dày | thickness | `Width` | Double | Walls |
| chiều dài | length | `Length` | Double | |
| diện tích | area | `Area` | Double | m² |
| thể tích | volume | `Volume` | Double | m³ |
| chu vi | perimeter | `Perimeter` | Double | |
| cao độ đáy | base offset | `Base Offset` | Double | |
| cao độ đỉnh | top offset | `Top Offset` | Double | |
| tầng | level | `Level` | ElementId | |
| loại vật liệu / cấu trúc | structural material | `Structural Material` | ElementId | |
| vật liệu kết cấu | structural material | `Structural Material` | ElementId | |
| phân khu / bộ phận | department | `Department` | String | Rooms |
| số phòng | room number | `Number` | String | Rooms |
| diện tích phòng | room area | `Area` | Double | Rooms |
| giai đoạn thi công | phase created | `Phase Created` | ElementId | |
| giai đoạn phá dỡ | phase demolished | `Phase Demolished` | ElementId | |
| mô tả | description | `Description` | String | Types |
| nhà sản xuất | manufacturer | `Manufacturer` | String | Types |
| model | model | `Model` | String | Types |
| kiểu / loại | type | type parameter | — | |
| họ | family | family | — | |

---

## Operators (Toán tử so sánh)

| Tiếng Việt | English operator | Revit filter op |
|---|---|---|
| bằng / là | equals | `eq` |
| không bằng / khác | not equals | `neq` |
| lớn hơn | greater than | `gt` |
| nhỏ hơn | less than | `lt` |
| lớn hơn hoặc bằng / tối thiểu | greater than or equal | `gte` |
| nhỏ hơn hoặc bằng / tối đa | less than or equal | `lte` |
| chứa / có | contains | `contains` |
| trống / chưa điền | is empty | custom check |
| không trống / đã điền | is not empty | custom check |

---

## Common Commands (Câu lệnh mẫu)

| Câu lệnh tiếng Việt | Intent JSON |
|---|---|
| Liệt kê tất cả tường | `{action: "list", category: "OST_Walls"}` |
| Tìm tất cả phòng chưa có tên | `{action: "find", category: "OST_Rooms", filters: [{param:"Name", op:"eq", value:""}]}` |
| Đặt cấp chống cháy = 60 cho tất cả cửa thoát hiểm | `{action: "set_parameter_batch", category: "OST_Doors", filter: ..., param: "Fire Rating", value: "60"}` |
| Kiểm tra tường có bề dày < 200mm | `{action: "compliance", category: "OST_Walls", assert: "Width < 0.2"}` |
| Đổi tên phòng 101 thành Phòng họp | `{action: "set_parameter", id: 101, param: "Name", value: "Phòng họp"}` |

---

## Notes cho LLM prompt

Khi parse câu lệnh tiếng Việt, model PHẢI:
1. Map danh từ chỉ cấu kiện → `BuiltInCategory` chính xác (dùng bảng trên).
2. Map tên tham số → Revit parameter name chính xác (dùng bảng trên).
3. KHÔNG tự tạo ra ElementId hoặc parameter name không có trong schema của model.
4. Khi không chắc → gọi tool `clarify` hỏi lại người dùng bằng tiếng Việt.
5. Echo-back luôn dùng format: `"Hiểu là: [mô tả tiếng Việt] / Understood: [EN professional term]"`.
