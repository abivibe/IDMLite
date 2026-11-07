namespace IDMLite.Services;

public class LanguageOption
{
    public string Code { get; }
    public string NativeName { get; }

    public LanguageOption(string code, string nativeName)
    {
        Code = code;
        NativeName = nativeName;
    }

    public override string ToString() => NativeName;
}

public class LocalizationManager
{
    private static readonly Lazy<LocalizationManager> LazyInstance = new(() => new LocalizationManager());
    public static LocalizationManager Instance => LazyInstance.Value;

    public string CurrentLanguage { get; private set; } = "en";
    public event EventHandler? LanguageChanged;

    public static readonly List<LanguageOption> SupportedLanguages = new()
    {
        new("en", "English"),
        new("tr", "Türkçe (Turkish)"),
        new("de", "Deutsch (German)"),
        new("fr", "Français (French)"),
        new("es", "Español (Spanish)"),
        new("it", "Italiano (Italian)"),
        new("pt", "Português (Portuguese)"),
        new("ru", "Русский (Russian)"),
        new("ar", "العربية (Arabic)"),
        new("ja", "日本語 (Japanese)"),
        new("zh", "简体中文 (Chinese)")
    };

    private readonly Dictionary<string, Dictionary<string, string>> _translations = new();

    public LocalizationManager()
    {
        InitializeDictionaries();
    }

    public void SetLanguage(string langCode)
    {
        if (string.IsNullOrWhiteSpace(langCode) || !_translations.ContainsKey(langCode))
        {
            langCode = "en";
        }

        CurrentLanguage = langCode;
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyDictionary<string, string> GetCurrentDictionary()
    {
        return _translations.TryGetValue(CurrentLanguage, out var dict) ? dict : _translations["en"];
    }

    public string Get(string key, string fallback = "")
    {
        if (_translations.TryGetValue(CurrentLanguage, out var dict) && dict.TryGetValue(key, out var val))
        {
            return val;
        }

        if (_translations.TryGetValue("en", out var enDict) && enDict.TryGetValue(key, out var enVal))
        {
            return enVal;
        }

        return string.IsNullOrEmpty(fallback) ? key : fallback;
    }

    private void InitializeDictionaries()
    {
        // 1. Türkçe (tr)
        _translations["tr"] = new Dictionary<string, string>
        {
            ["Str_AppTitle"] = "IDMLite - Modern Internet Download Manager",
            ["Str_File"] = "Dosya",
            ["Str_NewDownload"] = "Yeni İndirme Ekle...",
            ["Str_Exit"] = "Çıkış",
            ["Str_Downloads"] = "İndirilenler",
            ["Str_Resume"] = "Devam Et",
            ["Str_Pause"] = "Durdur",
            ["Str_StopAll"] = "Tümünü Durdur",
            ["Str_ResumeAll"] = "Tümünü Devam Ettir",
            ["Str_Delete"] = "Sil",
            ["Str_DeleteAll"] = "❌ Dosyayı ve Listeyi Sil",
            ["Str_Tools"] = "Araçlar",
            ["Str_Settings"] = "Seçenekler & Ayarlar...",
            ["Str_Extension"] = "Tarayıcı Eklentisi...",
            ["Str_Help"] = "Yardım",
            ["Str_About"] = "IDMLite Hakkında...",
            ["Str_Support"] = "Support",
            ["Str_AddUrl"] = "URL Ekle",
            ["Str_ShowInFolder"] = "Klasörde Göster",
            ["Str_OpenFile"] = "Dosyayı Aç",
            ["Str_Details"] = "Detaylar & Segmentler...",
            ["Str_VerifySha256"] = "SHA256 Bütünlük Doğrula",
            ["Str_CopyUrl"] = "Bağlantıyı Kopyala",
            ["Str_TrayMinimize"] = "Tepsiye Küçült",
            ["Str_MinimizeToTray"] = "Sistem tepsisine (Tray) küçült",
            ["Str_Categories"] = "KATEGORİLER",
            ["Str_AllDownloads"] = "Tüm İndirmeler",
            ["Str_Unfinished"] = "⏳ Tamamlanmamış",
            ["Str_Finished"] = "Tamamlananlar",
            ["Str_FileTypes"] = "Dosya Türleri",
            ["Str_Compressed"] = "Sıkıştırılmış",
            ["Str_Documents"] = "Belgeler",
            ["Str_Music"] = "Müzik",
            ["Str_Video"] = "Video",
            ["Str_Programs"] = "Programlar",
            ["Str_Other"] = "Diğer",
            ["Str_Queues"] = "Kuyruklar",
            ["Str_MainQueue"] = "Ana Kuyruk",
            ["Str_ColFileName"] = "Dosya Adı",
            ["Str_ColSize"] = "Boyut",
            ["Str_ColStatus"] = "Durum",
            ["Str_ColProgress"] = "İlerleme",
            ["Str_ColSpeed"] = "Hız",
            ["Str_ColTimeLeft"] = "Kalan Süre",
            ["Str_ColParts"] = "Parça",
            ["Str_ColDateAdded"] = "Tarih",
            ["Str_TotalSpeed"] = "⬇️ Toplam Hız:",
            ["Str_ActiveDownloads"] = "Aktif:",
            ["Str_Total"] = "Toplam:",
            ["Str_BrowserBridge"] = "Tarayıcı Köprüsü: Port 41718 Aktif",
            ["Str_SpeedLimit"] = "Hız Sınırı:",
            ["Str_Unlimited"] = "Sınırsız",
            ["Str_SearchPlaceholder"] = "Dosya adı veya bağlantı ile filtrele...",
            ["Str_FilesCount"] = "dosya",
            ["Str_AddDlgTitle"] = "Yeni İndirme Başlat - IDMLite",
            ["Str_AddDlgHeading"] = "Yeni Dosya İndir",
            ["Str_AddDlgSubheading"] = "İndirme bağlantısını ve hedef klasörü belirleyin",
            ["Str_UrlAddress"] = "Adres (URL):",
            ["Str_Category"] = "Kategori:",
            ["Str_Size"] = "Boyut:",
            ["Str_Determining"] = "Belirleniyor...",
            ["Str_PreviewUpdating"] = "İndirmeye başlayabilirsiniz. Kalite ve boyut bilgisi geldikçe burada güncellenir.",
            ["Str_PreviewDeferred"] = "Bazı video bilgileri şu anda alınamadı. İndirmeyi yine de başlatabilirsiniz.",
            ["Str_Unknown"] = "Bilinmiyor",
            ["Str_SizeUnavailable"] = "Boyut bilgisi yok",
            ["Str_SaveFileName"] = "Dosya Adı:",
            ["Str_SaveDirectory"] = "Kayıt Yeri:",
            ["Str_Browse"] = "Gözat...",
            ["Str_DownloadNow"] = "Hemen İndir",
            ["Str_DownloadLater"] = "Daha Sonra İndir",
            ["Str_Cancel"] = "İptal",
            ["Str_StreamFormat"] = "İndirme Türü:",
            ["Str_VideoFormat"] = "Video (MP4)",
            ["Str_AudioFormat"] = "Ses (MP3)",
            ["Str_ProgDlgTitle"] = "İndirme Durumu - IDMLite",
            ["Str_Status"] = "Durum:",
            ["Str_Connecting"] = "Sunucuya bağlanıyor...",
            ["Str_Downloading"] = "İndiriliyor",
            ["Str_Paused"] = "Duraklatıldı",
            ["Str_Completed"] = "İndirme Tamamlandı!",
            ["Str_Verified"] = "Doğrulandı ✅",
            ["Str_Error"] = "Hata:",
            ["Str_Assembling"] = "Parçalar birleştiriliyor...",
            ["Str_Verifying"] = "Bütünlük sınanıyor...",
            ["Str_Ratio"] = "İndirilen / Toplam Boyut",
            ["Str_TransferRate"] = "Aktarım Hızı",
            ["Str_Eta"] = "Kalan Süre",
            ["Str_ConnectionSegments"] = "Eşzamanlı Parça / Bağlantı Segmentleri (Multi-threaded Range Chunks)",
            ["Str_SendToMain"] = "📥 Ana Pencereye Gönder",
            ["Str_Close"] = "Kapat",
            ["Str_CopyChecksum"] = "📋 Kopyala",
            ["Str_SettingsTitle"] = "Ayarlar & Seçenekler - IDMLite",
            ["Str_TabGeneral"] = "Genel",
            ["Str_TabConnection"] = "Bağlantı & Motor",
            ["Str_TabFolders"] = "Kayıt Klasörleri",
            ["Str_TabSpeed"] = "Hız Sınırı",
            ["Str_Language"] = "Uygulama Dili (Language):",
            ["Str_Theme"] = "Arayüz Teması:",
            ["Str_DarkAmoled"] = "AMOLED Koyu Tema (Windows 11)",
            ["Str_Light"] = "Açık Tema (Light Mode)",
            ["Str_StartWithWindows"] = "Windows başladığında IDMLite'ı otomatik çalıştır",
            ["Str_CloseToTray"] = "Kapatıldığında sistem tepsisine (Tray) küçült",
            ["Str_ShowDownloadDialog"] = "Yeni indirme yakalandığında indirme penceresini göster",
            ["Str_MaxConcurrent"] = "Maksimum Eşzamanlı İndirme Sayısı:",
            ["Str_MaxThreads"] = "Dosya Başına Maksimum Parça / Bağlantı (Thread):",
            ["Str_EnableSpeedLimit"] = "İndirme hızını sınırla",
            ["Str_Save"] = "Kaydet",
            ["Str_TrayShow"] = "IDMLite'ı Aç / Göster",
            ["Str_TrayResumeAll"] = "Tümünü Başlat",
            ["Str_TrayPauseAll"] = "Tümünü Durdur",
            ["Str_TrayExit"] = "Çıkış"
        };

        // 2. English (en)
        _translations["en"] = new Dictionary<string, string>
        {
            ["Str_AppTitle"] = "IDMLite - Modern Internet Download Manager",
            ["Str_File"] = "File",
            ["Str_NewDownload"] = "Add New Download...",
            ["Str_Exit"] = "Exit",
            ["Str_Downloads"] = "Downloads",
            ["Str_Resume"] = "Resume",
            ["Str_Pause"] = "Pause",
            ["Str_StopAll"] = "Stop All",
            ["Str_ResumeAll"] = "Resume All",
            ["Str_Delete"] = "Delete",
            ["Str_DeleteAll"] = "❌ Delete File & Entry",
            ["Str_Tools"] = "Tools",
            ["Str_Settings"] = "Options & Settings...",
            ["Str_Extension"] = "Browser Extension...",
            ["Str_Help"] = "Help",
            ["Str_About"] = "About IDMLite...",
            ["Str_Support"] = "Support",
            ["Str_AddUrl"] = "Add URL",
            ["Str_ShowInFolder"] = "Show in Folder",
            ["Str_OpenFile"] = "Open File",
            ["Str_Details"] = "Details & Segments...",
            ["Str_VerifySha256"] = "Verify SHA256 Integrity",
            ["Str_CopyUrl"] = "Copy URL",
            ["Str_TrayMinimize"] = "Minimize to Tray",
            ["Str_MinimizeToTray"] = "Minimize to System Tray",
            ["Str_Categories"] = "CATEGORIES",
            ["Str_AllDownloads"] = "All Downloads",
            ["Str_Unfinished"] = "⏳ Unfinished",
            ["Str_Finished"] = "Finished",
            ["Str_FileTypes"] = "File Types",
            ["Str_Compressed"] = "Compressed",
            ["Str_Documents"] = "Documents",
            ["Str_Music"] = "Music",
            ["Str_Video"] = "Video",
            ["Str_Programs"] = "Programs",
            ["Str_Other"] = "Other",
            ["Str_Queues"] = "Queues",
            ["Str_MainQueue"] = "Main Queue",
            ["Str_ColFileName"] = "File Name",
            ["Str_ColSize"] = "Size",
            ["Str_ColStatus"] = "Status",
            ["Str_ColProgress"] = "Progress",
            ["Str_ColSpeed"] = "Speed",
            ["Str_ColTimeLeft"] = "Time Left",
            ["Str_ColParts"] = "Parts",
            ["Str_ColDateAdded"] = "Date Added",
            ["Str_TotalSpeed"] = "⬇️ Total Speed:",
            ["Str_ActiveDownloads"] = "Active:",
            ["Str_Total"] = "Total:",
            ["Str_BrowserBridge"] = "Browser Bridge: Port 41718 Active",
            ["Str_SpeedLimit"] = "Speed Limit:",
            ["Str_Unlimited"] = "Unlimited",
            ["Str_SearchPlaceholder"] = "Filter by filename or URL...",
            ["Str_FilesCount"] = "files",
            ["Str_AddDlgTitle"] = "Start New Download - IDMLite",
            ["Str_AddDlgHeading"] = "Download New File",
            ["Str_AddDlgSubheading"] = "Specify download link and target destination",
            ["Str_UrlAddress"] = "Address (URL):",
            ["Str_Category"] = "Category:",
            ["Str_Size"] = "Size:",
            ["Str_Determining"] = "Determining...",
            ["Str_PreviewUpdating"] = "You can start downloading now. Quality and size update here as they become available.",
            ["Str_PreviewDeferred"] = "Some video details are unavailable. You can still start the download.",
            ["Str_Unknown"] = "Unknown",
            ["Str_SizeUnavailable"] = "Size unavailable",
            ["Str_SaveFileName"] = "File Name:",
            ["Str_SaveDirectory"] = "Save To:",
            ["Str_Browse"] = "Browse...",
            ["Str_DownloadNow"] = "Download Now",
            ["Str_DownloadLater"] = "Download Later",
            ["Str_Cancel"] = "Cancel",
            ["Str_StreamFormat"] = "Download Type:",
            ["Str_VideoFormat"] = "Video (MP4)",
            ["Str_AudioFormat"] = "Audio (MP3)",
            ["Str_ProgDlgTitle"] = "Download Status - IDMLite",
            ["Str_Status"] = "Status:",
            ["Str_Connecting"] = "Connecting to server...",
            ["Str_Downloading"] = "Downloading",
            ["Str_Paused"] = "Paused",
            ["Str_Completed"] = "Download Completed!",
            ["Str_Verified"] = "Verified ✅",
            ["Str_Error"] = "Error:",
            ["Str_Assembling"] = "Assembling segments...",
            ["Str_Verifying"] = "Verifying integrity...",
            ["Str_Ratio"] = "Downloaded / Total Size",
            ["Str_TransferRate"] = "Transfer Rate",
            ["Str_Eta"] = "Time Remaining",
            ["Str_ConnectionSegments"] = "Multi-threaded Range Chunks",
            ["Str_SendToMain"] = "📥 Send to Main Window",
            ["Str_Close"] = "Close",
            ["Str_CopyChecksum"] = "📋 Copy",
            ["Str_SettingsTitle"] = "Settings & Options - IDMLite",
            ["Str_TabGeneral"] = "General",
            ["Str_TabConnection"] = "Connection & Engine",
            ["Str_TabFolders"] = "Download Folders",
            ["Str_TabSpeed"] = "Speed Limiter",
            ["Str_Language"] = "Application Language:",
            ["Str_Theme"] = "UI Theme:",
            ["Str_DarkAmoled"] = "AMOLED Dark Theme (Windows 11)",
            ["Str_Light"] = "Light Mode",
            ["Str_StartWithWindows"] = "Start IDMLite automatically with Windows",
            ["Str_CloseToTray"] = "Minimize to system tray when closed",
            ["Str_ShowDownloadDialog"] = "Show download dialog on interception",
            ["Str_MaxConcurrent"] = "Max Concurrent Downloads:",
            ["Str_MaxThreads"] = "Max Connections / Threads per File:",
            ["Str_EnableSpeedLimit"] = "Enable speed limiter",
            ["Str_Save"] = "Save",
            ["Str_TrayShow"] = "Open / Show IDMLite",
            ["Str_TrayResumeAll"] = "Resume All",
            ["Str_TrayPauseAll"] = "Pause All",
            ["Str_TrayExit"] = "Exit"
        };

        // 3. Deutsch (de)
        CloneWithOverrides("de", "en", new()
        {
            ["Str_File"] = "_Datei",
            ["Str_NewDownload"] = "_Neuer Download...",
            ["Str_Exit"] = "_Beenden",
            ["Str_Downloads"] = "_Downloads",
            ["Str_Resume"] = "_Fortsetzen",
            ["Str_Pause"] = "_Anhalten",
            ["Str_StopAll"] = "_Alle stoppen",
            ["Str_ResumeAll"] = "_Alle fortsetzen",
            ["Str_Delete"] = "_Löschen",
            ["Str_Tools"] = "_Werkzeuge",
            ["Str_Settings"] = "_Optionen & Einstellungen...",
            ["Str_Extension"] = "_Browser-Erweiterung...",
            ["Str_Help"] = "_Hilfe",
            ["Str_About"] = "_Über IDMLite...",
            ["Str_AddUrl"] = "URL hinzufügen",
            ["Str_ShowInFolder"] = "Im Ordner anzeigen",
            ["Str_OpenFile"] = "Datei öffnen",
            ["Str_TrayMinimize"] = "In Infobereich",
            ["Str_Categories"] = "KATEGORIEN",
            ["Str_AllDownloads"] = "Alle Downloads",
            ["Str_Unfinished"] = "⏳ Nicht abgeschlossen",
            ["Str_Finished"] = "Abgeschlossen",
            ["Str_FileTypes"] = "Dateitypen",
            ["Str_Compressed"] = "Komprimiert",
            ["Str_Documents"] = "Dokumente",
            ["Str_Music"] = "Musik",
            ["Str_Video"] = "Video",
            ["Str_Programs"] = "Programme",
            ["Str_Other"] = "Andere",
            ["Str_ColFileName"] = "Dateiname",
            ["Str_ColSize"] = "Größe",
            ["Str_ColStatus"] = "Status",
            ["Str_ColProgress"] = "Fortschritt",
            ["Str_ColSpeed"] = "Geschwindigkeit",
            ["Str_ColTimeLeft"] = "Restzeit",
            ["Str_DownloadNow"] = "Jetzt herunterladen",
            ["Str_DownloadLater"] = "Später herunterladen",
            ["Str_Cancel"] = "Abbrechen",
            ["Str_Close"] = "Schließen",
            ["Str_SendToMain"] = "📥 Zum Hauptfenster senden",
            ["Str_Language"] = "Sprache (Language):",
            ["Str_Save"] = "Speichern"
        });

        // 4. Français (fr)
        CloneWithOverrides("fr", "en", new()
        {
            ["Str_File"] = "_Fichier",
            ["Str_NewDownload"] = "_Nouveau téléchargement...",
            ["Str_Exit"] = "_Quitter",
            ["Str_Downloads"] = "_Téléchargements",
            ["Str_Resume"] = "_Reprendre",
            ["Str_Pause"] = "_Pause",
            ["Str_StopAll"] = "_Tout arrêter",
            ["Str_ResumeAll"] = "_Tout reprendre",
            ["Str_Delete"] = "_Supprimer",
            ["Str_Tools"] = "_Outils",
            ["Str_Settings"] = "_Options & Paramètres...",
            ["Str_Extension"] = "_Extension navigateur...",
            ["Str_Help"] = "_Aide",
            ["Str_About"] = "_À propos d'IDMLite...",
            ["Str_AddUrl"] = "Ajouter URL",
            ["Str_ShowInFolder"] = "Afficher dans le dossier",
            ["Str_OpenFile"] = "Ouvrir fichier",
            ["Str_TrayMinimize"] = "Réduire en zone",
            ["Str_Categories"] = "CATÉGORIES",
            ["Str_AllDownloads"] = "Tous les téléchargements",
            ["Str_Unfinished"] = "⏳ Non terminé",
            ["Str_Finished"] = "Terminé",
            ["Str_FileTypes"] = "Types de fichiers",
            ["Str_Compressed"] = "Compressé",
            ["Str_Documents"] = "Documents",
            ["Str_Music"] = "Musique",
            ["Str_Video"] = "Vidéo",
            ["Str_Programs"] = "Programmes",
            ["Str_Other"] = "Autre",
            ["Str_ColFileName"] = "Nom de fichier",
            ["Str_ColSize"] = "Taille",
            ["Str_ColStatus"] = "Statut",
            ["Str_ColProgress"] = "Progression",
            ["Str_ColSpeed"] = "Vitesse",
            ["Str_ColTimeLeft"] = "Temps restant",
            ["Str_DownloadNow"] = "Télécharger maintenant",
            ["Str_DownloadLater"] = "Télécharger plus tard",
            ["Str_Cancel"] = "Annuler",
            ["Str_Close"] = "Fermer",
            ["Str_SendToMain"] = "📥 Envoyer à la fenêtre principale",
            ["Str_Language"] = "Langue (Language):",
            ["Str_Save"] = "Enregistrer"
        });

        // 5. Español (es)
        CloneWithOverrides("es", "en", new()
        {
            ["Str_File"] = "_Archivo",
            ["Str_NewDownload"] = "_Nueva descarga...",
            ["Str_Exit"] = "_Salir",
            ["Str_Downloads"] = "_Descargas",
            ["Str_Resume"] = "_Reanudar",
            ["Str_Pause"] = "_Pausar",
            ["Str_StopAll"] = "_Detener todo",
            ["Str_ResumeAll"] = "_Reanudar todo",
            ["Str_Delete"] = "_Eliminar",
            ["Str_Tools"] = "_Herramientas",
            ["Str_Settings"] = "_Opciones y configuración...",
            ["Str_Extension"] = "_Extensión del navegador...",
            ["Str_Help"] = "_Ayuda",
            ["Str_About"] = "_Acerca de IDMLite...",
            ["Str_AddUrl"] = "Añadir URL",
            ["Str_ShowInFolder"] = "Mostrar en carpeta",
            ["Str_OpenFile"] = "Abrir archivo",
            ["Str_TrayMinimize"] = "Minimizar a bandeja",
            ["Str_Categories"] = "CATEGORÍAS",
            ["Str_AllDownloads"] = "Todas las descargas",
            ["Str_Unfinished"] = "⏳ No terminadas",
            ["Str_Finished"] = "Completadas",
            ["Str_FileTypes"] = "Tipos de archivo",
            ["Str_Compressed"] = "Comprimidos",
            ["Str_Documents"] = "Documentos",
            ["Str_Music"] = "Música",
            ["Str_Video"] = "Vídeo",
            ["Str_Programs"] = "Programas",
            ["Str_Other"] = "Otros",
            ["Str_ColFileName"] = "Nombre de archivo",
            ["Str_ColSize"] = "Tamaño",
            ["Str_ColStatus"] = "Estado",
            ["Str_ColProgress"] = "Progreso",
            ["Str_ColSpeed"] = "Velocidad",
            ["Str_ColTimeLeft"] = "Tiempo restante",
            ["Str_DownloadNow"] = "Descargar ahora",
            ["Str_DownloadLater"] = "Descargar más tarde",
            ["Str_Cancel"] = "Cancelar",
            ["Str_Close"] = "Cerrar",
            ["Str_SendToMain"] = "📥 Enviar a la ventana principal",
            ["Str_Language"] = "Idioma (Language):",
            ["Str_Save"] = "Guardar"
        });

        // 6. Italiano (it)
        CloneWithOverrides("it", "en", new()
        {
            ["Str_File"] = "_File",
            ["Str_NewDownload"] = "_Nuovo download...",
            ["Str_Exit"] = "_Esci",
            ["Str_Downloads"] = "_Download",
            ["Str_Resume"] = "_Riprendi",
            ["Str_Pause"] = "_Pausa",
            ["Str_StopAll"] = "_Interrompi tutto",
            ["Str_ResumeAll"] = "_Riprendi tutto",
            ["Str_Delete"] = "_Elimina",
            ["Str_Tools"] = "_Strumenti",
            ["Str_Settings"] = "_Opzioni & Impostazioni...",
            ["Str_Extension"] = "_Estensione browser...",
            ["Str_Help"] = "_Aiuto",
            ["Str_About"] = "_Informazioni su IDMLite...",
            ["Str_AddUrl"] = "Aggiungi URL",
            ["Str_ShowInFolder"] = "Mostra nella cartella",
            ["Str_OpenFile"] = "Apri file",
            ["Str_TrayMinimize"] = "Riduci a icona",
            ["Str_Categories"] = "CATEGORIE",
            ["Str_AllDownloads"] = "Tutti i download",
            ["Str_Unfinished"] = "⏳ Incompleti",
            ["Str_Finished"] = "Completati",
            ["Str_FileTypes"] = "Tipi di file",
            ["Str_Compressed"] = "Compressi",
            ["Str_Documents"] = "Documenti",
            ["Str_Music"] = "Musica",
            ["Str_Video"] = "Video",
            ["Str_Programs"] = "Programmi",
            ["Str_Other"] = "Altro",
            ["Str_ColFileName"] = "Nome file",
            ["Str_ColSize"] = "Dimensione",
            ["Str_ColStatus"] = "Stato",
            ["Str_ColProgress"] = "Progresso",
            ["Str_ColSpeed"] = "Velocità",
            ["Str_ColTimeLeft"] = "Tempo rimasto",
            ["Str_DownloadNow"] = "Scarica ora",
            ["Str_DownloadLater"] = "Scarica più tardi",
            ["Str_Cancel"] = "Annulla",
            ["Str_Close"] = "Chiudi",
            ["Str_SendToMain"] = "📥 Invia alla finestra principale",
            ["Str_Language"] = "Lingua (Language):",
            ["Str_Save"] = "Salva"
        });

        // 7. Português (pt)
        CloneWithOverrides("pt", "en", new()
        {
            ["Str_File"] = "_Arquivo",
            ["Str_NewDownload"] = "_Novo Download...",
            ["Str_Exit"] = "_Sair",
            ["Str_Downloads"] = "_Downloads",
            ["Str_Resume"] = "_Retomar",
            ["Str_Pause"] = "_Pausar",
            ["Str_StopAll"] = "_Parar tudo",
            ["Str_ResumeAll"] = "_Retomar tudo",
            ["Str_Delete"] = "_Excluir",
            ["Str_Tools"] = "_Ferramentas",
            ["Str_Settings"] = "_Opções e Configurações...",
            ["Str_Extension"] = "_Extensão do Navegador...",
            ["Str_Help"] = "_Ajuda",
            ["Str_About"] = "_Sobre o IDMLite...",
            ["Str_AddUrl"] = "Adicionar URL",
            ["Str_ShowInFolder"] = "Mostrar na pasta",
            ["Str_OpenFile"] = "Abrir arquivo",
            ["Str_TrayMinimize"] = "Minimizar para bandeja",
            ["Str_Categories"] = "CATEGORIAS",
            ["Str_AllDownloads"] = "Todos os downloads",
            ["Str_Unfinished"] = "⏳ Incompletos",
            ["Str_Finished"] = "Concluídos",
            ["Str_FileTypes"] = "Tipos de arquivo",
            ["Str_Compressed"] = "Compactados",
            ["Str_Documents"] = "Documentos",
            ["Str_Music"] = "Música",
            ["Str_Video"] = "Vídeo",
            ["Str_Programs"] = "Programas",
            ["Str_Other"] = "Outro",
            ["Str_ColFileName"] = "Nome do arquivo",
            ["Str_ColSize"] = "Tamanho",
            ["Str_ColStatus"] = "Status",
            ["Str_ColProgress"] = "Progresso",
            ["Str_ColSpeed"] = "Velocidade",
            ["Str_ColTimeLeft"] = "Tempo restante",
            ["Str_DownloadNow"] = "Baixar agora",
            ["Str_DownloadLater"] = "Baixar mais tarde",
            ["Str_Cancel"] = "Cancelar",
            ["Str_Close"] = "Fechar",
            ["Str_SendToMain"] = "📥 Enviar para janela principal",
            ["Str_Language"] = "Idioma (Language):",
            ["Str_Save"] = "Salvar"
        });

        // 8. Русский (ru)
        CloneWithOverrides("ru", "en", new()
        {
            ["Str_File"] = "_Файл",
            ["Str_NewDownload"] = "_Добавить закачку...",
            ["Str_Exit"] = "_Выход",
            ["Str_Downloads"] = "_Закачки",
            ["Str_Resume"] = "_Возобновить",
            ["Str_Pause"] = "_Пауза",
            ["Str_StopAll"] = "_Остановить все",
            ["Str_ResumeAll"] = "_Возобновить все",
            ["Str_Delete"] = "_Удалить",
            ["Str_Tools"] = "_Инструменты",
            ["Str_Settings"] = "_Параметры & Настройки...",
            ["Str_Extension"] = "_Расширение для браузера...",
            ["Str_Help"] = "_Справка",
            ["Str_About"] = "_О программе IDMLite...",
            ["Str_AddUrl"] = "Добавить URL",
            ["Str_ShowInFolder"] = "Показать в папке",
            ["Str_OpenFile"] = "Открыть файл",
            ["Str_TrayMinimize"] = "Свернуть в трей",
            ["Str_Categories"] = "КАТЕГОРИИ",
            ["Str_AllDownloads"] = "Все загрузки",
            ["Str_Unfinished"] = "⏳ Незавершенные",
            ["Str_Finished"] = "Завершенные",
            ["Str_FileTypes"] = "Типы файлов",
            ["Str_Compressed"] = "Архивы",
            ["Str_Documents"] = "Документы",
            ["Str_Music"] = "Музыка",
            ["Str_Video"] = "Видео",
            ["Str_Programs"] = "Программы",
            ["Str_Other"] = "Разное",
            ["Str_ColFileName"] = "Имя файла",
            ["Str_ColSize"] = "Размер",
            ["Str_ColStatus"] = "Состояние",
            ["Str_ColProgress"] = "Прогресс",
            ["Str_ColSpeed"] = "Скорость",
            ["Str_ColTimeLeft"] = "Осталось",
            ["Str_DownloadNow"] = "Скачать сейчас",
            ["Str_DownloadLater"] = "Скачать позже",
            ["Str_Cancel"] = "Отмена",
            ["Str_Close"] = "Закрыть",
            ["Str_SendToMain"] = "📥 В главное окно",
            ["Str_Language"] = "Язык (Language):",
            ["Str_Save"] = "Сохранить"
        });

        // 9. العربية (ar)
        CloneWithOverrides("ar", "en", new()
        {
            ["Str_File"] = "_ملف",
            ["Str_NewDownload"] = "_إضافة تنزيل جديد...",
            ["Str_Exit"] = "_خروج",
            ["Str_Downloads"] = "_التنزيلات",
            ["Str_Resume"] = "_استئناف",
            ["Str_Pause"] = "_إيقاف مؤقت",
            ["Str_StopAll"] = "_إيقاف الكل",
            ["Str_ResumeAll"] = "_استئناف الكل",
            ["Str_Delete"] = "_حذف",
            ["Str_Tools"] = "_أدوات",
            ["Str_Settings"] = "_الخيارات والإعدادات...",
            ["Str_Extension"] = "_إضافة المتصفح...",
            ["Str_Help"] = "_مساعدة",
            ["Str_About"] = "_حول IDMLite...",
            ["Str_AddUrl"] = "إضافة رابط",
            ["Str_ShowInFolder"] = "عرض في المجلد",
            ["Str_OpenFile"] = "فتح الملف",
            ["Str_TrayMinimize"] = "تصغير لشريط المهام",
            ["Str_Categories"] = "الفئات",
            ["Str_AllDownloads"] = "كافة التنزيلات",
            ["Str_Unfinished"] = "⏳ غير مكتمل",
            ["Str_Finished"] = "المكتملة",
            ["Str_FileTypes"] = "أنواع الملفات",
            ["Str_Compressed"] = "مضغوط",
            ["Str_Documents"] = "مستندات",
            ["Str_Music"] = "موسيقى",
            ["Str_Video"] = "فيديو",
            ["Str_Programs"] = "برامج",
            ["Str_Other"] = "أخرى",
            ["Str_ColFileName"] = "اسم الملف",
            ["Str_ColSize"] = "الحجم",
            ["Str_ColStatus"] = "الحالة",
            ["Str_ColProgress"] = "التقدم",
            ["Str_ColSpeed"] = "السرعة",
            ["Str_ColTimeLeft"] = "الوقت المتبقي",
            ["Str_DownloadNow"] = "تنزيل الآن",
            ["Str_DownloadLater"] = "تنزيل لاحقاً",
            ["Str_Cancel"] = "إلغاء",
            ["Str_Close"] = "إغلاق",
            ["Str_SendToMain"] = "📥 إرسال إلى النافذة الرئيسية",
            ["Str_Language"] = "اللغة (Language):",
            ["Str_Save"] = "حفظ"
        });

        // 10. 日本語 (ja)
        CloneWithOverrides("ja", "en", new()
        {
            ["Str_File"] = "_ファイル",
            ["Str_NewDownload"] = "_新規ダウンロード追加...",
            ["Str_Exit"] = "_終了",
            ["Str_Downloads"] = "_ダウンロード",
            ["Str_Resume"] = "_再開",
            ["Str_Pause"] = "_一時停止",
            ["Str_StopAll"] = "_すべて停止",
            ["Str_ResumeAll"] = "_すべて再開",
            ["Str_Delete"] = "_削除",
            ["Str_Tools"] = "_ツール",
            ["Str_Settings"] = "_オプションと設定...",
            ["Str_Extension"] = "_ブラウザ拡張機能...",
            ["Str_Help"] = "_ヘルプ",
            ["Str_About"] = "_IDMLiteについて...",
            ["Str_AddUrl"] = "URL追加",
            ["Str_ShowInFolder"] = "フォルダで表示",
            ["Str_OpenFile"] = "ファイルを開く",
            ["Str_TrayMinimize"] = "トレイに最小化",
            ["Str_Categories"] = "カテゴリー",
            ["Str_AllDownloads"] = "すべてのダウンロード",
            ["Str_Unfinished"] = "⏳ 未完了",
            ["Str_Finished"] = "完了",
            ["Str_FileTypes"] = "ファイルタイプ",
            ["Str_Compressed"] = "圧縮ファイル",
            ["Str_Documents"] = "ドキュメント",
            ["Str_Music"] = "音楽",
            ["Str_Video"] = "動画",
            ["Str_Programs"] = "プログラム",
            ["Str_Other"] = "その他",
            ["Str_ColFileName"] = "ファイル名",
            ["Str_ColSize"] = "サイズ",
            ["Str_ColStatus"] = "状態",
            ["Str_ColProgress"] = "進行状況",
            ["Str_ColSpeed"] = "速度",
            ["Str_ColTimeLeft"] = "残り時間",
            ["Str_DownloadNow"] = "今すぐダウンロード",
            ["Str_DownloadLater"] = "後でダウンロード",
            ["Str_Cancel"] = "キャンセル",
            ["Str_Close"] = "閉じる",
            ["Str_SendToMain"] = "📥 メインウィンドウに送信",
            ["Str_Language"] = "言語 (Language):",
            ["Str_Save"] = "保存"
        });

        // 11. 简体中文 (zh)
        CloneWithOverrides("zh", "en", new()
        {
            ["Str_File"] = "_文件",
            ["Str_NewDownload"] = "_新建下载任务...",
            ["Str_Exit"] = "_退出",
            ["Str_Downloads"] = "_下载任务",
            ["Str_Resume"] = "_继续",
            ["Str_Pause"] = "_暂停",
            ["Str_StopAll"] = "_全部停止",
            ["Str_ResumeAll"] = "_全部继续",
            ["Str_Delete"] = "_删除",
            ["Str_Tools"] = "_工具",
            ["Str_Settings"] = "_选项与设置...",
            ["Str_Extension"] = "_浏览器扩展...",
            ["Str_Help"] = "_帮助",
            ["Str_About"] = "_关于 IDMLite...",
            ["Str_AddUrl"] = "添加URL",
            ["Str_ShowInFolder"] = "打开所在文件夹",
            ["Str_OpenFile"] = "打开文件",
            ["Str_TrayMinimize"] = "最小化到托盘",
            ["Str_Categories"] = "分类",
            ["Str_AllDownloads"] = "全部下载",
            ["Str_Unfinished"] = "⏳ 未完成",
            ["Str_Finished"] = "已完成",
            ["Str_FileTypes"] = "文件类型",
            ["Str_Compressed"] = "压缩包",
            ["Str_Documents"] = "文档",
            ["Str_Music"] = "音乐",
            ["Str_Video"] = "视频",
            ["Str_Programs"] = "软件程序",
            ["Str_Other"] = "其他",
            ["Str_ColFileName"] = "文件名",
            ["Str_ColSize"] = "大小",
            ["Str_ColStatus"] = "状态",
            ["Str_ColProgress"] = "进度",
            ["Str_ColSpeed"] = "速度",
            ["Str_ColTimeLeft"] = "剩余时间",
            ["Str_DownloadNow"] = "立即下载",
            ["Str_DownloadLater"] = "稍后下载",
            ["Str_Cancel"] = "取消",
            ["Str_Close"] = "关闭",
            ["Str_SendToMain"] = "📥 发送到主窗口",
            ["Str_Language"] = "界面语言 (Language):",
            ["Str_Save"] = "保存"
        });

        // Ensure no UI button or menu string starts with a literal '_'
        foreach (var dict in _translations.Values)
        {
            foreach (var key in dict.Keys.ToList())
            {
                if (dict[key].StartsWith('_'))
                {
                    dict[key] = dict[key].TrimStart('_');
                }
            }
        }
    }

    private void CloneWithOverrides(string targetLang, string baseLang, Dictionary<string, string> overrides)
    {
        var dict = new Dictionary<string, string>(_translations[baseLang]);
        foreach (var kvp in overrides)
        {
            dict[kvp.Key] = kvp.Value.TrimStart('_');
        }
        _translations[targetLang] = dict;
    }
}
