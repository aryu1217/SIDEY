using System;
using System.Linq;

namespace Sidey.Installer
{
    // Also compiled with the Windows .NET Framework compiler: do not require .NET 10.
    public sealed class InstallerLanguage
    {
        public readonly int Id;
        public readonly string EnglishName;
        public readonly string DisplayName;

        public InstallerLanguage(int id, string englishName, string displayName)
        {
            Id = id;
            EnglishName = englishName;
            DisplayName = displayName;
        }

        public override string ToString()
        {
            return DisplayName;
        }
    }

    public static class InstallerLanguages
    {
        public static InstallerLanguage[] All()
        {
            return new[]
            {
                new InstallerLanguage(2052, "Chinese (Simplified)", "简体中文"),
                new InstallerLanguage(1028, "Chinese (Traditional)", "繁體中文"),
                new InstallerLanguage(1033, "English", "English"),
                new InstallerLanguage(1041, "Japanese", "日本語"),
                new InstallerLanguage(1042, "Korean", "한국어"),
                new InstallerLanguage(1049, "Russian", "Русский"),
                new InstallerLanguage(1058, "Ukrainian", "Українська"),
            };
        }

        public static int MatchSystemLanguage(int windowsLanguage)
        {
            switch (windowsLanguage & 0x3ff)
            {
                case 0x09:
                    return 1033; // English, any region
                case 0x11:
                    return 1041;
                case 0x12:
                    return 1042;
                case 0x19:
                    return 1049;
                case 0x22:
                    return 1058;
                case 0x04:
                    // Taiwan, Hong Kong, Macau, or the neutral zh-Hant locale.
                    return windowsLanguage == 0x0404 || windowsLanguage == 0x0c04
                        || windowsLanguage == 0x1404 || windowsLanguage == 0x7c04
                            ? 1028
                            : 2052;
                default:
                    return 0;
            }
        }

        public static string UpdateLaunchWarning(int windowsLanguage)
        {
            switch (MatchSystemLanguage(windowsLanguage))
            {
                case 1041:
                    return "このファイルはSIDEYの自動更新用です。SIDEYアプリ内で更新するか、手動で更新する場合はSetupインストーラーをダウンロードして実行してください。";
                case 1042:
                    return "이 파일은 SIDEY의 자동 업데이트용입니다. SIDEY 앱에서 업데이트하거나, 수동으로 업데이트하려면 Setup 설치 파일을 다운로드하여 실행하세요.";
                case 1049:
                    return "Этот файл используется SIDEY для автоматических обновлений. Обновите SIDEY через приложение или скачайте и запустите установщик Setup для обновления вручную.";
                case 1058:
                    return "Цей файл використовується SIDEY для автоматичних оновлень. Оновіть SIDEY через застосунок або завантажте й запустіть інсталятор Setup для оновлення вручну.";
                case 2052:
                    return "此文件用于 SIDEY 的自动更新。请在 SIDEY 应用内更新，或下载并运行 Setup 安装程序以手动更新。";
                case 1028:
                    return "此檔案用於 SIDEY 的自動更新。請在 SIDEY 應用程式內更新，或下載並執行 Setup 安裝程式以手動更新。";
                default:
                    return "This file is used by SIDEY for automatic updates. Update from within SIDEY, or download and run Setup for a manual update.";
            }
        }

        public static InstallerLanguage[] Ordered(int windowsLanguage)
        {
            int systemLanguage = MatchSystemLanguage(windowsLanguage);
            return All()
                .OrderBy(language => language.Id == systemLanguage ? 0 : 1)
                .ThenBy(language => language.EnglishName, StringComparer.Ordinal)
                .ToArray();
        }

        public static int DefaultSelection(int windowsLanguage, int savedLanguage)
        {
            if (All().Any(language => language.Id == savedLanguage))
            {
                return savedLanguage;
            }

            int systemLanguage = MatchSystemLanguage(windowsLanguage);
            return systemLanguage == 0 ? 1033 : systemLanguage;
        }

        public static string AppLanguage(int installerLanguage)
        {
            switch (installerLanguage)
            {
                case 1033:
                    return "en-US";
                case 1041:
                    return "ja-JP";
                case 1042:
                    return "ko-KR";
                case 1049:
                    return "ru-RU";
                case 1058:
                    return "uk-UA";
                case 2052:
                    return "zh-CN";
                case 1028:
                    return "zh-TW";
                default:
                    return string.Empty;
            }
        }
    }
}
