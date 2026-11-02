namespace AnonymMailClient.Localization;

/// <summary>
/// User-facing UI strings, Turkish by default. <see cref="LocManager"/> reflection-writes these
/// fields to <c>lang.tr.xml</c> when it is missing, and loads matching XML entries when a language
/// is selected. Keep each public string field mutable so the existing loader can update it.
/// Add the same key and matching format placeholders to lang.en.xml for every new field.
/// </summary>
internal static class Strings
{
    private const bool IsLogEnabled = true;

    // Application.
    public static string AppTitle = "Örnek Uygulama";
    public static string AppDescription = "Uygulama açıklaması";

    // Language.
    public static string SettingLanguage = "Dil";
    public static string LanguageSystem = "Windows'u izle";

    // Settings.
    public static string SettingsTitle = "Ayarlar";
    public static string SettingExampleText = "Örnek metin ayarı";
    public static string SettingExampleNumber = "Örnek sayı ayarı";
    public static string SettingExampleEnabled = "Örnek özellik açık";
    public static string SettingsSaved = "Ayarlar kaydedildi.";

    // Commands.
    public static string ButtonSave = "Kaydet";
    public static string ButtonCancel = "Vazgeç";
    public static string ButtonRetry = "Yeniden dene";
    public static string ButtonClose = "Kapat";
    public static string ButtonOpenFolder = "Klasörü aç";

    // Operation status.
    public static string StatusReady = "Hazır";
    public static string StatusWorking = "İşlem sürüyor.";
    public static string StatusCompleted = "İşlem tamamlandı.";
    public static string StatusCanceled = "İşlem iptal edildi.";
    public static string StatusFailed = "İşlem tamamlanamadı.";
    public static string ProgressItems = "{0} / {1} öğe işlendi.";

    // User-editable text supports escaped newlines in the XML files.
    public static string ExampleMultiline = "İlk satır\nİkinci satır";
}
