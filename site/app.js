(() => {
  "use strict";

  const translations = {
    ru: {
      skip: "Перейти к содержимому",
      home: "GlimHUD — на главную",
      navigation: "Навигация",
      navFeatures: "Возможности",
      navPersonalize: "Оформление",
      navDownload: "Скачать",
      language: "Язык",
      githubAria: "GlimHUD на GitHub",
      eyebrow: "БЕСПЛАТНЫЙ ОВЕРЛЕЙ ДЛЯ WINDOWS · ОТКРЫТЫЙ КОД",
      heroTitle: "Всё, что нужно.",
      heroTitleAccent: "Поверх игры.",
      heroDescription: "Ваша музыка, производительность и стиль. Одна прозрачная панель, с которой можно оставаться в игре.",
      downloadWindows: "Скачать для Windows",
      viewGithub: "Открыть GitHub ↗",
      platformShort: "Windows 10 / 11 · x64",
      free: "Бесплатно. Без аккаунта.",
      showcaseTitle: "Мало места. Много полезного.",
      sample: "ПРЕВЬЮ ИНТЕРФЕЙСА · ПРИМЕР ДАННЫХ",
      overlayScroll: "Превью GlimHUD; на небольшом экране панель можно прокрутить по горизонтали",
      overlayAlt: "Панель GlimHUD с управлением музыкой, FPS, нагрузкой CPU, GPU, памятью и скоростями сетевого трафика, с голубым и розовым свечением. Значения приведены для примера.",
      showcaseCaption: "Прозрачная панель. Больше игры.",
      showcaseHint: "Перемещаемая · Прозрачная · Всегда под рукой",
      mobileHint: "Прокрутите, чтобы увидеть панель →",
      atGlance: "Коротко о программе",
      factNative: "Нативное приложение Windows",
      factInjection: "Без внедрения DLL в игры",
      factLicense: "Лицензия MIT",
      factLanguages: "Английский и русский",
      featuresEyebrow: "ОСТАВАЙТЕСЬ В ИГРЕ",
      featuresTitle: "Меньше переключений.\nБольше игры.",
      featuresIntro: "Всё полезное — рядом. Всё остальное — на втором плане.",
      musicTitle: "Музыка под контролем.",
      musicBody: "Название трека, обложка, пауза и переключение песен — прямо во время игры. Прокрутка в области плеера меняет громкость музыки.",
      musicDetail: "Работает с совместимыми медиаплеерами Windows, включая Яндекс Музыку.",
      musicVisual: "МУЗЫКА ВСЕГДА ПОД РУКОЙ",
      statsTitle: "Знайте, как работает игра.",
      statsBody: "FPS, нагрузка CPU и GPU, температуры и память. По желанию добавьте скорость загрузки и отдачи; в настройках — трафик за сеанс и график за 60 секунд.",
      statsDetail: "Сеть показывает один адаптер, включая другие приложения и локальный трафик. Модуль изначально выключен. FPS и датчики зависят от конфигурации ПК.",
      sampleShort: "ПРИМЕР ИЗМЕРЕНИЯ",
      focusTitle: "Рядом, когда нужна.",
      focusBody: "Автоматически показывайте панель в игре, полностью скрывайте музыкальный блок или включайте пропуск кликов. Направляющие помогут удобно расположить оверлей.",
      focusDetail: "Для игр в оконном режиме и без рамки.",
      focusVisual: "ВАШЕ ПРОСТРАНСТВО, ВАША ПАНЕЛЬ",
      styleEyebrow: "СДЕРЖАННО ИЛИ ЯРКО. РЕШАТЬ ВАМ.",
      styleTitle: "Добавьте\nнемного света.",
      styleBody: "Лёгкое сияние или яркие краски. Rainbow Wave и Neon Flow подсвечивают панель снаружи и изнутри, а при желании реагируют на музыку.",
      styleColor: "Акцент Windows или собственные цвета",
      styleTransparency: "Цвет текста и фона, прозрачность и масштаб",
      styleAudio: "Реакция на музыку с настройкой чувствительности",
      styleNote: "Свечение необязательно и изначально выключено. Здесь показана анимация; в приложении реакция на музыку следует за уровнем звука выбранного плеера.",
      glowPreview: "ПРЕВЬЮ СВЕЧЕНИЯ",
      glowStyle: "Стиль свечения в превью",
      glowOff: "Выкл.",
      shortcutsEyebrow: "БЕЗ ЛИШНИХ ДВИЖЕНИЙ МЫШЬЮ",
      shortcutsTitle: "Управление\nпод рукой.",
      shortcutsBody: "Горячие клавиши работают, даже если панель скрыта или заблокирована. Громкость меняется только у выбранного приложения.",
      allShortcuts: "Все горячие клавиши в README ↗",
      shortcutPlay: "Пауза / воспроизведение",
      shortcutVolume: "Громкость музыки",
      shortcutHide: "Показать / скрыть панель",
      shortcutLock: "Блокировка / перемещение",
      openSource: "БЕСПЛАТНО · ОТКРЫТЫЙ КОД",
      downloadTitle: "Больше обзора.\nБольше вашего стиля.",
      downloadBody: "Скачайте GlimHUD для Windows. Установщик для обычной установки или ZIP для портативного запуска.",
      downloadInstaller: "Скачать установщик",
      downloadPortable: "Портативный .zip",
      releaseNotes: "Что нового ↗",
      requirementsLabel: "СИСТЕМНЫЕ ТРЕБОВАНИЯ",
      requirements: "Windows 10 версии 2004 и новее / Windows 11 · x64. Скачиваемые сборки уже включают среду .NET.",
      signingLabel: "ПОДПИСЬ ПРИЛОЖЕНИЯ",
      signing: "Установщик пока не подписан. Windows может показать предупреждение безопасности или SmartScreen. Скачивайте программу только из GitHub Releases этого проекта.",
      communityEyebrow: "РАЗРАБАТЫВАЕМ ОТКРЫТО",
      communityTitle: "Есть идея? Нашли ошибку?",
      communityBody: "GlimHUD — свободное ПО под лицензией MIT. Исходный код, разработка и поддержка находятся на GitHub.",
      openIssue: "Сделаем лучше вместе ↗",
      footerNote: "Без аккаунтов, рекламы и телеметрии.",
      projectLinks: "Ссылки проекта",
      source: "Исходный код",
      privacy: "Приватность",
      support: "Поддержка"
    }
  };

  const elements = [...document.querySelectorAll("[data-i18n], [data-i18n-aria], [data-i18n-alt]")];
  const english = {};
  elements.forEach((element) => {
    if (element.dataset.i18n) {
      english[element.dataset.i18n] = [...element.childNodes]
        .map((node) => node.nodeName === "BR" ? "\n" : node.textContent).join("");
    }
    if (element.dataset.i18nAria) english[element.dataset.i18nAria] = element.getAttribute("aria-label");
    if (element.dataset.i18nAlt) english[element.dataset.i18nAlt] = element.getAttribute("alt");
  });
  translations.en = english;

  function setLanguage(language) {
    const selected = language === "ru" ? "ru" : "en";
    const dictionary = translations[selected];
    document.documentElement.lang = selected;
    document.title = selected === "ru" ? "GlimHUD — Поверх игры" : "GlimHUD — Above your game";
    elements.forEach((element) => {
      const key = element.dataset.i18n;
      if (key && dictionary[key] !== undefined) {
        const lines = dictionary[key].split("\n");
        element.replaceChildren();
        lines.forEach((line, index) => {
          if (index > 0) element.append(document.createElement("br"));
          element.append(document.createTextNode(line));
        });
      }
      const ariaKey = element.dataset.i18nAria;
      if (ariaKey && dictionary[ariaKey]) element.setAttribute("aria-label", dictionary[ariaKey]);
      const altKey = element.dataset.i18nAlt;
      if (altKey && dictionary[altKey]) element.setAttribute("alt", dictionary[altKey]);
    });
    document.querySelectorAll("[data-lang]").forEach((button) => {
      button.setAttribute("aria-pressed", String(button.dataset.lang === selected));
    });
    try { localStorage.setItem("glimhud.language", selected); } catch { /* Storage is optional. */ }
  }

  document.querySelectorAll("[data-lang]").forEach((button) => {
    button.addEventListener("click", () => setLanguage(button.dataset.lang));
  });
  let savedLanguage = "en";
  try { savedLanguage = localStorage.getItem("glimhud.language") || "en"; } catch { /* Keep the English default. */ }
  setLanguage(savedLanguage);

  document.querySelectorAll("[data-glow]").forEach((button) => {
    button.addEventListener("click", () => {
      document.querySelector(".glow-demo").dataset.style = button.dataset.glow;
      document.querySelectorAll("[data-glow]").forEach((option) => {
        option.setAttribute("aria-pressed", String(option === button));
      });
    });
  });

  // This optional local file is generated with the release. No GitHub API or analytics requests.
  if (location.protocol === "https:" || location.protocol === "http:") {
    fetch("release.json", { cache: "no-cache", credentials: "omit", referrerPolicy: "no-referrer" })
      .then((response) => response.ok ? response.json() : null)
      .then((release) => {
        if (!release || typeof release.version !== "string" || !/^\d+\.\d+\.\d+(?:[-+][\w.-]+)?$/.test(release.version)) return;
        document.querySelectorAll(".release-version").forEach((element) => {
          element.textContent = `v${release.version}`;
          element.hidden = false;
        });
      })
      .catch(() => { /* Downloads remain available without release metadata. */ });
  }
})();
