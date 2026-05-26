# Magic Cursor 🪄

<p align="center">
  <img src="app_logo.png" alt="Magic Cursor Logo" width="160" height="160" />
</p>

**Magic Cursor** is a premium, lightweight Windows productivity tool inspired by the concept of Google's Chromebook/Googlebook "Magic Pointer" implementation. Re-imagined and engineered from the ground up for Windows 10/11 using C# and WinUI 3, Magic Cursor lets you trigger advanced on-screen actions using local OCR (Optical Character Recognition) and Google's Gemini multimodal AI.

By shaking your mouse rapidly, a glassmorphic HUD menu appears directly under your cursor. You can then highlight any region on your screen and ask Gemini to write emails, debug code, analyze images, summarize documents, or explain complex layouts.

---

## 📸 Screenshots (In Action)

### 1. Magic Menu Overlay (Mouse-Shake Summoned)
Shake your cursor rapidly anywhere on the screen to summon the glassmorphic HUD context panel instantly.
![Magic Menu Overlay](screenshots/magic%20cursor%20context%20menue.png)

### 2. Highlighting & AI Scanning
Click and drag to select any text or image area on your screen. The border pulses to indicate AI capture.
![Highlighting and Scanning](screenshots/magic%20cursor%20analysing.png)

### 3. Rich Markdown AI Output
Get rich, beautifully formatted insights, summaries, or answers rendered directly in the glassmorphic modal.
![AI Response Output](screenshots/Magic%20cursor%20response%20output.png)

### 4. Configuration & Startup Settings
Configure your Gemini API key and enable launching silently on Windows boot.
![Settings Panel](screenshots/Magic%20cursor%20settings.png)

---

## 🚀 How to Install

### Method 1: Download Standalone ZIP (GitHub Releases)
1. Go to the [GitHub Releases Page](https://github.com/muhammadhaseebiqbal-dev/Magic-Cursor/releases).
2. Download the latest release package: `MagicCursor_v0.0.3_win-x64.zip`.
3. Extract it and run `MagicCursor.exe`. 
   *(Note: This package is fully self-contained and does not require any installers or runtime installs).*

### Method 2: Microsoft Store
* *Coming Soon!* We are currently prepping our package manifest to make Magic Cursor available directly via the Microsoft Store for easy installation and automated background updates.

---

## 🛠 How to Compile From Source

### Prerequisites
- **Windows 10/11**
- **.NET 10 SDK** (or later)
- **Visual Studio 2022** (with the *.NET Desktop Development* workload installed, including *Windows App SDK C# Tools*)

### Build Steps
1. **Clone the repository**:
   ```powershell
   git clone https://github.com/muhammadhaseebiqbal-dev/Magic-Cursor.git
   cd Magic-Cursor
   ```

2. **Restore dependencies**:
   ```powershell
   dotnet restore
   ```

3. **Build the project**:
   ```powershell
   dotnet build -c Release
   ```

4. **Publish as a Standalone Portable Package**:
   To generate a zero-dependency self-contained folder that can be distributed anywhere:
   ```powershell
   dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:WindowsPackageType=None
   ```
   The compiled executable and required native DLLs will be generated in `bin\Release\net10.0-windows10.0.26100.0\win-x64\publish\`.

---

## 💡 Key Use Cases

* **📧 Email & Writing Help**: Highlight a rough draft on your screen and ask: *"rewrite this email professionally"* or *"translate this message to Japanese"*.
* **🖼 Multimodal Image Analysis**: Drag-select any image, diagram, chart, or logo on your screen and ask: *"what color is this button?"*, *"explain this graph"*, or *"generate HTML code to reproduce this layout"*. (Image-based queries are automatically routed to Gemini’s vision pipeline).
* **🐞 Developer Debugging**: Highlight compilation errors, system logs, or crash tracebacks directly in your IDE or terminal, and ask: *"how do I fix this bug?"*.
* **📝 Fast Screen OCR**: Extract text from images, videos, or PDFs where copy-paste is disabled.

---

## 🤝 Contribution & Collaboration Guide

We welcome open-source contributions! Whether you want to fix a bug, improve performance, or enhance the design:

1. **Fork** the repository on GitHub.
2. Create a feature branch: `git checkout -b feature/amazing-feature`.
3. **Commit** your changes: `git commit -m "Add some amazing feature"`.
4. **Push** to the branch: `git push origin feature/amazing-feature`.
5. Open a **Pull Request** for review.

### 📬 Get in Touch / Collaborate
If you want to join our core development team, discuss partnerships, or propose major integrations, feel free to reach out using this email template:

```
Subject: Collaboration Inquiry - Magic Cursor
To: contact@yourdomain.com

Hi Magic Cursor Team,

I recently started using Magic Cursor and am absolutely impressed by the glassmorphic UI, global mouse-shake hook, and hybrid OCR/vision routing. I would love to contribute to the project!

Specifically, I am interested in helping with:
- [e.g. Adding custom hotkeys or actions]
- [e.g. Optimizing the Windows hook performance]
- [e.g. Store packaging and MSIX setup]

Let me know how I can best get involved, or if you have a developer community channel (like Discord/Slack) I could join.

Best regards,
[Your Name]
[Your GitHub Profile / Portfolio Link]
```

---

## 📄 License
This project is licensed under the MIT License - see the LICENSE file for details.
