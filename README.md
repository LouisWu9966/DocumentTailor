<div align="center">
  <img src="src/DocumentTailor/Assets/DocumentTailor.png" width="128" alt="Document Tailor icon">
  <h1>Document Tailor</h1>
  <p>離線優先的 Windows 文件轉換、郵件整理與 PDF 合併工具。</p>
</div>

## 特色

- 將 Word、PDF、Outlook MSG、CSV、Excel 與文字檔轉換為 Markdown。
- 一次選取多份文件，依加入順序彙整成單一 Markdown。
- 多份 Outlook MSG 可依郵件主旨建立章節，保留寄件者、收件者、副本、寄送時間、本文與附件清單。
- Word、PDF、MSG、CSV、Excel 等不同格式可混合排序後整合成單一 Markdown。
- 多份 PDF 也能直接合併成一份 PDF。
- 支援拖放匯入、清單排序、進度提示、取消作業與 Markdown 即時預覽。
- 所有文件均在本機處理，不會上傳至雲端。
- 科技感深色介面、首頁動畫與響應式視窗配置。

## 支援格式

| 輸入格式 | 副檔名 | 輸出／用途 |
|---|---|---|
| Microsoft Word | `.docx` | Markdown |
| PDF | `.pdf` | Markdown、PDF 合併 |
| Outlook 郵件 | `.msg` | Markdown、郵件彙整 |
| CSV | `.csv` | Markdown 表格 |
| Microsoft Excel | `.xlsx`, `.xlsm` | Markdown 表格 |
| 純文字／Markdown | `.txt`, `.md` | Markdown、文件彙整 |

## 使用方式

### 文件轉 Markdown

1. 開啟「文件轉 Markdown」。
2. 拖放文件，或按下「加入文件」多選匯入。
3. 選取文件並使用上移、下移調整章節順序。
4. 按下「轉換並儲存」。
5. 選擇輸出的 `.md` 路徑。

多選 `.msg` 時，每封郵件會以主旨建立獨立章節，再彙整成同一份 Markdown。

### PDF 合併

1. 開啟「PDF 合併」並加入至少兩份 PDF。
2. 使用上移、下移調整頁面合併順序。
3. 按下「合併並輸出 PDF」。

## 系統需求

- Windows 10 或 Windows 11，64 位元。
- 若使用 Release 中的 self-contained 單檔版本，不需要另外安裝 .NET Runtime。
- 從原始碼建置需要 [.NET SDK 9](https://dotnet.microsoft.com/download/dotnet/9.0)。

## 從原始碼建置

```powershell
dotnet restore .\src\DocumentTailor\DocumentTailor.csproj
dotnet build .\src\DocumentTailor\DocumentTailor.csproj -c Release
```

## 發佈單檔 EXE

```powershell
dotnet publish .\src\DocumentTailor\DocumentTailor.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -o .\publish\win-x64
```

輸出檔案為 `publish\win-x64\DocumentTailor.exe`。

## 執行煙霧測試

測試會建立實際的 DOCX、MSG、CSV、Excel 與 PDF 樣本，驗證格式轉換、多文件彙整及 PDF 合併頁數。

```powershell
dotnet run --project .\tests\DocumentTailor.SmokeTests\DocumentTailor.SmokeTests.csproj -c Release
```

## 專案結構

```text
DocumentTailor/
├─ src/DocumentTailor/                 WPF 桌面應用程式
│  ├─ Models/                          顯示模型
│  ├─ Services/                        文件解析與轉換服務
│  └─ Assets/                          正式應用程式圖示
├─ tests/DocumentTailor.SmokeTests/    端對端煙霧測試
└─ tools/                              圖示建置工具
```

## 已知限制

- 掃描型 PDF 若沒有文字層，目前只會輸出提示；尚未內建 OCR。
- Excel 支援 `.xlsx` 與 `.xlsm`，暫不支援舊式 `.xls`。
- MSG 附件會列出檔名，但不會自動解壓或另存附件。
- PDF 的複雜多欄版面、特殊字型或圖片式表格，可能需要人工調整 Markdown。

## 隱私

Document Tailor 不需要帳號，也不會將文件內容傳送至網路。所有解析、轉換與合併均在本機完成。

## 開發者

開發者：[@Louis](https://github.com/LouisWu9966)
