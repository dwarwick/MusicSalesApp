namespace MusicSalesApp.Common.Helpers;

/// <summary>
/// Inline SVG icon constants to avoid Font Awesome dependency issues with Blazor enhanced navigation.
/// All icons use viewBox="0 0 24 24" and fill="currentColor" so they inherit the parent's text color.
/// Use with MarkupString: @((MarkupString)SvgIcons.Play)
/// </summary>
public static class SvgIcons
{
    private const string Svg16 = "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='currentColor' width='16' height='16'>";
    private const string Svg14 = "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='currentColor' width='14' height='14'>";
    private const string End = "</svg>";

    // Media controls
    public const string Play = Svg16 + "<path d='M8 5v14l11-7z'/>" + End;
    public const string Pause = Svg16 + "<path d='M6 19h4V5H6v14zm8-14v14h4V5h-4z'/>" + End;
    public const string Stop = Svg16 + "<path d='M6 6h12v12H6z'/>" + End;

    // Actions
    public const string Eye = Svg16 + "<path d='M12 4.5C7 4.5 2.73 7.61 1 12c1.73 4.39 6 7.5 11 7.5s9.27-3.11 11-7.5c-1.73-4.39-6-7.5-11-7.5zM12 17c-2.76 0-5-2.24-5-5s2.24-5 5-5 5 2.24 5 5-2.24 5-5 5zm0-8c-1.66 0-3 1.34-3 3s1.34 3 3 3 3-1.34 3-3-1.34-3-3-3z'/>" + End;
    public const string Edit = Svg16 + "<path d='M3 17.25V21h3.75L17.81 9.94l-3.75-3.75L3 17.25zM20.71 7.04a1 1 0 0 0 0-1.41l-2.34-2.34a1 1 0 0 0-1.41 0l-1.83 1.83 3.75 3.75 1.83-1.83z'/>" + End;
    public const string Trash = Svg16 + "<path d='M6 19c0 1.1.9 2 2 2h8c1.1 0 2-.9 2-2V7H6v12zM19 4h-3.5l-1-1h-5l-1 1H5v2h14V4z'/>" + End;
    public const string Plus = Svg16 + "<path d='M19 13h-6v6h-2v-6H5v-2h6V5h2v6h6v2z'/>" + End;
    public const string Times = Svg16 + "<path d='M19 6.41L17.59 5 12 10.59 6.41 5 5 6.41 10.59 12 5 17.59 6.41 19 12 13.41 17.59 19 19 17.59 13.41 12z'/>" + End;
    public const string Link = Svg14 + "<path d='M3.9 12c0-1.71 1.39-3.1 3.1-3.1h4V7H7c-2.76 0-5 2.24-5 5s2.24 5 5 5h4v-1.9H7c-1.71 0-3.1-1.39-3.1-3.1zM8 13h8v-2H8v2zm9-6h-4v1.9h4c1.71 0 3.1 1.39 3.1 3.1s-1.39 3.1-3.1 3.1h-4V17h4c2.76 0 5-2.24 5-5s-2.24-5-5-5z'/>" + End;

    // Navigation
    public const string House = Svg16 + "<path d='M10 20v-6h4v6h5v-8h3L12 3 2 12h3v8z'/>" + End;
    public const string Music = Svg16 + "<path d='M12 3v10.55c-.59-.34-1.27-.55-2-.55-2.21 0-4 1.79-4 4s1.79 4 4 4 4-1.79 4-4V7h4V3h-6z'/>" + End;
    public const string List = Svg16 + "<path d='M3 13h2v-2H3v2zm0 4h2v-2H3v2zm0-8h2V7H3v2zm4 4h14v-2H7v2zm0 4h14v-2H7v2zM7 7v2h14V7H7z'/>" + End;
    public const string UserGear = Svg16 + "<path d='M12 12c2.21 0 4-1.79 4-4s-1.79-4-4-4-4 1.79-4 4 1.79 4 4 4zm-6 8v-2c0-2.21 3.58-4 6-4h.26a6.5 6.5 0 0 1-.26-1.83V12c-1.06-.49-3.35-1-4-1-2.67 0-8 1.34-8 4v2h6zm17.9-3.5l-1.18-.5c.08-.33.13-.67.13-1s-.05-.67-.13-1l1.18-.5-.5-1.18-1.18.5A3.49 3.49 0 0 0 21 12.5l.5-1.18-1.18-.5-.5 1.18c-.33-.08-.67-.13-1-.13s-.67.05-1 .13l-.5-1.18-1.18.5.5 1.18A3.49 3.49 0 0 0 16 14c0 .34.05.67.13 1l-1.18.5.5 1.18 1.18-.5c.34.43.76.81 1.24 1.09l-.5 1.18 1.18.5.5-1.18c.33.08.67.13 1 .13s.67-.05 1-.13l.5 1.18 1.18-.5-.5-1.18c.48-.28.9-.66 1.24-1.09l1.18.5.5-1.18zM19.05 17c-1.1 0-2-.9-2-2s.9-2 2-2 2 .9 2 2-.9 2-2 2z'/>" + End;

    // Auth
    public const string ArrowRightFromBracket = Svg16 + "<path d='M17 7l-1.41 1.41L18.17 11H8v2h10.17l-2.58 2.58L17 17l5-5zM4 5h8V3H4c-1.1 0-2 .9-2 2v14c0 1.1.9 2 2 2h8v-2H4V5z'/>" + End;
    public const string ArrowRightToBracket = Svg16 + "<path d='M11 7L9.6 8.4l2.6 2.6H2v2h10.2L9.6 15.6 11 17l5-5zm9 12h-8v2h8c1.1 0 2-.9 2-2V5c0-1.1-.9-2-2-2h-8v2h8v14z'/>" + End;
    public const string UserPlus = Svg16 + "<path d='M15 12c2.21 0 4-1.79 4-4s-1.79-4-4-4-4 1.79-4 4 1.79 4 4 4zm-9-2V7H4v3H1v2h3v3h2v-3h3v-2H6zm9 4c-2.67 0-8 1.34-8 4v2h16v-2c0-2.66-5.33-4-8-4z'/>" + End;

    // Creator/Admin
    public const string CloudArrowUp = Svg16 + "<path d='M19.35 10.04A7.49 7.49 0 0 0 12 4C9.11 4 6.6 5.64 5.35 8.04A5.99 5.99 0 0 0 0 14c0 3.31 2.69 6 6 6h13c2.76 0 5-2.24 5-5 0-2.64-2.05-4.78-4.65-4.96zM14 13v4h-4v-4H7l5-5 5 5h-3z'/>" + End;
    public const string ChartColumn = Svg16 + "<path d='M10 20h4V4h-4v16zm-6 0h4v-8H4v8zM16 9v11h4V9h-4z'/>" + End;
    public const string FileAudio = Svg16 + "<path d='M14 2H6c-1.1 0-2 .9-2 2v16c0 1.1.9 2 2 2h12c1.1 0 2-.9 2-2V8l-6-6zm-1 11v5c0 1.1-.9 2-2 2s-2-.9-2-2 .9-2 2-2c.37 0 .7.11 1 .28V11h3v2h-2zM13 9V3.5L18.5 9H13z'/>" + End;
    public const string MoneyBillWave = Svg16 + "<path d='M11.8 10.9c-2.27-.59-3-1.2-3-2.15 0-1.09 1.01-1.85 2.7-1.85 1.78 0 2.44.85 2.5 2.1h2.21c-.07-1.72-1.12-3.3-3.21-3.81V3h-3v2.16c-1.94.42-3.5 1.68-3.5 3.61 0 2.31 1.91 3.46 4.7 4.13 2.5.6 3 1.48 3 2.41 0 .69-.49 1.79-2.7 1.79-2.06 0-2.87-.92-2.98-2.1h-2.2c.12 2.19 1.76 3.42 3.68 3.83V21h3v-2.15c1.95-.37 3.5-1.5 3.5-3.55 0-2.84-2.43-3.81-4.7-4.4z'/>" + End;
    public const string MoneyBillTransfer = Svg16 + "<path d='M19 14V6c0-1.1-.9-2-2-2H3c-1.1 0-2 .9-2 2v8c0 1.1.9 2 2 2h14c1.1 0 2-.9 2-2zm-2 0H3V6h14v8zm-7-7c-1.66 0-3 1.34-3 3s1.34 3 3 3 3-1.34 3-3-1.34-3-3-3zm13 0v11c0 1.1-.9 2-2 2H4v-2h17V7h2z'/>" + End;
    public const string ShieldHalved = Svg16 + "<path d='M12 1L3 5v6c0 5.55 3.84 10.74 9 12 5.16-1.26 9-6.45 9-12V5l-9-4zm0 10.99h7c-.53 4.12-3.28 7.79-7 8.94V12H5V6.3l7-3.11v8.8z'/>" + End;
    public const string ShieldVirus = Svg16 + "<path d='M12 1L3 5v6c0 5.55 3.84 10.74 9 12 5.16-1.26 9-6.45 9-12V5l-9-4zm0 2.18l7 3.12v4.7c0 4.83-3.13 9.37-7 10.93-3.87-1.56-7-6.1-7-10.93V6.3l7-3.12zM10 12a2 2 0 1 0 0-4 2 2 0 0 0 0 4zm4 2a1.5 1.5 0 1 0 0-3 1.5 1.5 0 0 0 0 3z'/>" + End;
    public const string UsersGear = Svg16 + "<path d='M9 12c2.21 0 4-1.79 4-4s-1.79-4-4-4-4 1.79-4 4 1.79 4 4 4zm0 2c-2.67 0-8 1.34-8 4v2h16v-2c0-2.66-5.33-4-8-4zm7.76-9.64l-1.18.5c-.33-.08-.67-.13-1-.13s-.67.05-1 .13l-.5-1.18-1.18.5.5 1.18c-.43.34-.81.76-1.09 1.24l-1.18-.5-.5 1.18 1.18.5c-.08.33-.13.67-.13 1s.05.67.13 1l-1.18.5.5 1.18 1.18-.5c.28.48.66.9 1.09 1.24l-.5 1.18 1.18.5.5-1.18c.33.08.67.13 1 .13s.67-.05 1-.13l.5 1.18 1.18-.5-.5-1.18c.48-.28.9-.66 1.24-1.09l1.18.5.5-1.18-1.18-.5c.08-.33.13-.67.13-1s-.05-.67-.13-1l1.18-.5-.5-1.18-1.18.5a3.49 3.49 0 0 0-1.24-1.09l.5-1.18zM14.58 9c-1.1 0-2-.9-2-2s.9-2 2-2 2 .9 2 2-.9 2-2 2z'/>" + End;
    public const string GaugeHigh = Svg16 + "<path d='M19.14 12.94c.04-.3.06-.61.06-.94 0-.32-.02-.64-.07-.94l2.03-1.58a.49.49 0 0 0 .12-.61l-1.92-3.32a.49.49 0 0 0-.59-.22l-2.39.96c-.5-.38-1.03-.7-1.62-.94l-.36-2.54a.48.48 0 0 0-.48-.41h-3.84c-.24 0-.43.17-.47.41l-.36 2.54c-.59.24-1.13.57-1.62.94l-2.39-.96a.49.49 0 0 0-.59.22L2.74 8.87c-.12.21-.08.47.12.61l2.03 1.58c-.05.3-.07.62-.07.94s.02.64.07.94l-2.03 1.58a.49.49 0 0 0-.12.61l1.92 3.32c.12.22.37.29.59.22l2.39-.96c.5.38 1.03.7 1.62.94l.36 2.54c.05.24.24.41.48.41h3.84c.24 0 .44-.17.47-.41l.36-2.54c.59-.24 1.13-.56 1.62-.94l2.39.96c.22.08.47 0 .59-.22l1.92-3.32c.12-.22.07-.47-.12-.61l-2.01-1.58zM12 15.6c-1.98 0-3.6-1.62-3.6-3.6s1.62-3.6 3.6-3.6 3.6 1.62 3.6 3.6-1.62 3.6-3.6 3.6z'/>" + End;
    public const string Gear = Svg16 + "<path d='M19.14 12.94c.04-.3.06-.61.06-.94 0-.32-.02-.64-.07-.94l2.03-1.58a.49.49 0 0 0 .12-.61l-1.92-3.32a.49.49 0 0 0-.59-.22l-2.39.96c-.5-.38-1.03-.7-1.62-.94l-.36-2.54a.48.48 0 0 0-.48-.41h-3.84c-.24 0-.43.17-.47.41l-.36 2.54c-.59.24-1.13.57-1.62.94l-2.39-.96a.49.49 0 0 0-.59.22L2.74 8.87c-.12.21-.08.47.12.61l2.03 1.58c-.05.3-.07.62-.07.94s.02.64.07.94l-2.03 1.58a.49.49 0 0 0-.12.61l1.92 3.32c.12.22.37.29.59.22l2.39-.96c.5.38 1.03.7 1.62.94l.36 2.54c.05.24.24.41.48.41h3.84c.24 0 .44-.17.47-.41l.36-2.54c.59-.24 1.13-.56 1.62-.94l2.39.96c.22.08.47 0 .59-.22l1.92-3.32c.12-.22.07-.47-.12-.61l-2.01-1.58zM12 15.6c-1.98 0-3.6-1.62-3.6-3.6s1.62-3.6 3.6-3.6 3.6 1.62 3.6 3.6-1.62 3.6-3.6 3.6z'/>" + End;
    public const string UserClock = Svg16 + "<path d='M16.5 13c-3.04 0-5.5 2.46-5.5 5.5s2.46 5.5 5.5 5.5 5.5-2.46 5.5-5.5-2.46-5.5-5.5-5.5zm.5 6h-1.5v-2.5H17V19h.01zm-8-5c2.21 0 4-1.79 4-4s-1.79-4-4-4-4 1.79-4 4 1.79 4 4 4zm0 2c-2.67 0-8 1.34-8 4v2h9.04A7.5 7.5 0 0 1 9 14.5V14z'/>" + End;
    public const string ClockRotateLeft = Svg16 + "<path d='M13 3a9 9 0 0 0-9 9H1l3.89 3.89.07.14L9 12H6c0-3.87 3.13-7 7-7s7 3.13 7 7-3.13 7-7 7c-1.93 0-3.68-.79-4.94-2.06l-1.42 1.42A8.954 8.954 0 0 0 13 21a9 9 0 0 0 0-18zm-1 5v5l4.28 2.54.72-1.21-3.5-2.08V8H12z'/>" + End;
    public const string Heart = Svg16 + "<path d='M12 21.35l-1.45-1.32C5.4 15.36 2 12.28 2 8.5 2 5.42 4.42 3 7.5 3c1.74 0 3.41.81 4.5 2.09C13.09 3.81 14.76 3 16.5 3 19.58 3 22 5.42 22 8.5c0 3.78-3.4 6.86-8.55 11.54L12 21.35z'/>" + End;
    public const string FileContract = Svg16 + "<path d='M14 2H6c-1.1 0-2 .9-2 2v16c0 1.1.9 2 2 2h12c1.1 0 2-.9 2-2V8l-6-6zm-1 7V3.5L18.5 9H13zM8 14h8v2H8v-2zm0 3h5v2H8v-2z'/>" + End;
    public const string FileSignature = Svg16 + "<path d='M14 2H6c-1.1 0-2 .9-2 2v16c0 1.1.9 2 2 2h12c1.1 0 2-.9 2-2V8l-6-6zm-1 7V3.5L18.5 9H13zm-2.5 6.5l-1.5 1.5H7v-2l4.5-4.5 2 2-3 3zm5.5-4l-1-1 1.41-1.41a.5.5 0 0 1 .71 0l.29.29a.5.5 0 0 1 0 .71L15 11.5z'/>" + End;
    public const string PaperPlane = Svg16 + "<path d='M2.01 21L23 12 2.01 3 2 10l15 2-15 2z'/>" + End;
    public const string CircleInfo = Svg16 + "<path d='M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm1 15h-2v-6h2v6zm0-8h-2V7h2v2z'/>" + End;
    public const string WandMagicSparkles = Svg16 + "<path d='M7.5 5.6L10 7 8.6 4.5 10 2 7.5 3.4 5 2l1.4 2.5L5 7zm12 9.8L17 14l1.4 2.5L17 19l2.5-1.4L22 19l-1.4-2.5L22 14zM22 2l-2.5 1.4L17 2l1.4 2.5L17 7l2.5-1.4L22 7l-1.4-2.5zm-7.63 5.29a1 1 0 0 0-1.41 0L1.29 18.96a1 1 0 0 0 0 1.41l2.34 2.34a1 1 0 0 0 1.41 0L16.71 11.04a1 1 0 0 0 0-1.41l-2.34-2.34zM5.71 20.71l-2.42-2.42L12 9.59l2.42 2.42-8.71 8.7z'/>" + End;
    public const string CheckCircle = Svg16 + "<path d='M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm-2 15l-5-5 1.41-1.41L10 14.17l7.59-7.59L19 8l-9 9z'/>" + End;
    public const string CropSimple = Svg16 + "<path d='M17 15h2V7c0-1.1-.9-2-2-2H9v2h8v8zM7 17V1H5v4H1v2h4v10c0 1.1.9 2 2 2h10v4h2v-4h4v-2H7z'/>" + End;
    public const string Image = Svg16 + "<path d='M21 19V5c0-1.1-.9-2-2-2H5c-1.1 0-2 .9-2 2v14c0 1.1.9 2 2 2h14c1.1 0 2-.9 2-2zM8.5 13.5l2.5 3.01L14.5 12l4.5 6H5l3.5-4.5z'/>" + End;
}
