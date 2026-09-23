/**
 * Enterprise Observatory — semantik renk token'ları
 *
 * Tek gerçek kaynağı. CSS değişkenleri ve doğrulayıcı bu dosyadan üretilir.
 *
 * Tasarım kuralı: bir durumun tek bir rengi YOKTUR; rol başına ayrı değeri
 * vardır. Önceki üründe tek renk hem metin hem nokta hem kenarlık olarak, hem
 * açık hem koyu temada kullanılıyordu ve açık temada 2.06:1'e kadar düşüyordu.
 * Beyaz üzerinde metin olarak okunan bir amber ile koyu zeminde nokta olarak
 * okunan amber aynı açıklıkta olamaz.
 *
 * Roller:
 *   surface  pill/satır arka planı
 *   border   nokta, kenarlık, bar dolgusu — sayfa zeminine karşı >= 3:1
 *   solid    yüksek vurgulu rozet dolgusu — kendi `on` rengiyle >= 4.5:1
 *   text     surface üzerindeki metin — surface'a karşı >= 4.5:1
 *
 * Renkler OKLCH olarak yazılır (ADR-0006, shadcn theming kuralı).
 */

/** @typedef {{l:number, c:number, h:number}} Oklch */

/** Eylem renkleri durum hue'larından uzak tutulur.
 *  Gerekçe: bir izleme ürününde renk = durum. Eylem butonu warning amber'ı
 *  veya info blue'su olursa operatör durum ile eylemi karıştırır.
 *  primary = violet (hiçbir durumun hue'su değil)
 *  destructive = critical ile aynı hue — yıkıcı eylem gerçekten tehlikelidir */
export const HUE = {
    healthy: 150,
    warning: 75,
    critical: 25,
    info: 245,
    unknown: 250, // düşük chroma ile nötr
    primary: 285,
};

export const dark = {
    page: { l: 0.17, c: 0.008, h: 250 },
    card: { l: 0.21, c: 0.010, h: 250 },
    foreground: { l: 0.95, c: 0.005, h: 250 },
    mutedForeground: { l: 0.72, c: 0.012, h: 250 },
    border: { l: 0.32, c: 0.012, h: 250 },
    // Mavi hue'nun sRGB gamut'u dar; yüksek L'de chroma düşürülmeli.
    focusRing: { l: 0.75, c: 0.105, h: 245 },

    status: {
        healthy: {
            surface: { l: 0.25, c: 0.035, h: HUE.healthy },
            border: { l: 0.62, c: 0.13, h: HUE.healthy },
            solid: { l: 0.65, c: 0.14, h: HUE.healthy },
            solidOn: { l: 0.17, c: 0.01, h: HUE.healthy },
            text: { l: 0.84, c: 0.11, h: HUE.healthy },
        },
        warning: {
            surface: { l: 0.26, c: 0.040, h: HUE.warning },
            border: { l: 0.70, c: 0.13, h: HUE.warning },
            solid: { l: 0.75, c: 0.14, h: HUE.warning },
            solidOn: { l: 0.17, c: 0.01, h: HUE.warning },
            text: { l: 0.86, c: 0.10, h: HUE.warning },
        },
        critical: {
            surface: { l: 0.26, c: 0.045, h: HUE.critical },
            border: { l: 0.60, c: 0.17, h: HUE.critical },
            solid: { l: 0.53, c: 0.18, h: HUE.critical },
            solidOn: { l: 0.98, c: 0.004, h: HUE.critical },
            text: { l: 0.82, c: 0.09, h: HUE.critical },
        },
        info: {
            surface: { l: 0.25, c: 0.040, h: HUE.info },
            border: { l: 0.63, c: 0.13, h: HUE.info },
            solid: { l: 0.49, c: 0.112, h: HUE.info },
            solidOn: { l: 0.98, c: 0.004, h: HUE.info },
            text: { l: 0.83, c: 0.075, h: HUE.info },
        },
        unknown: {
            surface: { l: 0.24, c: 0.010, h: HUE.unknown },
            border: { l: 0.58, c: 0.020, h: HUE.unknown },
            solid: { l: 0.55, c: 0.020, h: HUE.unknown },
            solidOn: { l: 0.98, c: 0.005, h: HUE.unknown },
            text: { l: 0.82, c: 0.015, h: HUE.unknown },
        },
    },

    // Koyu temada açık bir primary + koyu metin, ters kombinasyondan daha iyi
    // kontrast veriyor ve koyu zeminde daha okunur duruyor.
    primary: { l: 0.72, c: 0.13, h: HUE.primary },
    primaryOn: { l: 0.17, c: 0.010, h: HUE.primary },

    // ADR-0008 §2: yıkıcı eylem critical ile aynı hue'yu paylaşır. Değerler
    // critical.solid/solidOn ile birebir aynı — o çift zaten >= 4.5:1 doğrulanmış
    // (bkz. `${s}: solidOn / solid` kuralı), yeni bir renk icat etmeye gerek yok.
    destructive: { l: 0.53, c: 0.18, h: HUE.critical },
    destructiveOn: { l: 0.98, c: 0.004, h: HUE.critical },
};

export const light = {
    page: { l: 0.985, c: 0.002, h: 250 },
    card: { l: 1.0, c: 0, h: 250 },
    foreground: { l: 0.22, c: 0.015, h: 250 },
    mutedForeground: { l: 0.45, c: 0.018, h: 250 },
    border: { l: 0.90, c: 0.008, h: 250 },
    focusRing: { l: 0.50, c: 0.112, h: 245 },

    status: {
        healthy: {
            surface: { l: 0.96, c: 0.025, h: HUE.healthy },
            border: { l: 0.52, c: 0.13, h: HUE.healthy },
            solid: { l: 0.50, c: 0.13, h: HUE.healthy },
            solidOn: { l: 0.99, c: 0.005, h: HUE.healthy },
            text: { l: 0.40, c: 0.10, h: HUE.healthy },
        },
        warning: {
            // Önceki üründe en kötü durum buydu: 2.06:1.
            // Amber açık temada metin olarak kullanılamayacak kadar açık;
            // text rolü belirgin şekilde koyulaştırıldı.
            surface: { l: 0.96, c: 0.024, h: HUE.warning },
            border: { l: 0.58, c: 0.115, h: HUE.warning },
            solid: { l: 0.56, c: 0.110, h: HUE.warning },
            solidOn: { l: 0.99, c: 0, h: HUE.warning },
            text: { l: 0.42, c: 0.075, h: HUE.warning },
        },
        critical: {
            surface: { l: 0.96, c: 0.017, h: HUE.critical },
            border: { l: 0.54, c: 0.19, h: HUE.critical },
            solid: { l: 0.52, c: 0.20, h: HUE.critical },
            solidOn: { l: 0.99, c: 0, h: HUE.critical },
            text: { l: 0.44, c: 0.16, h: HUE.critical },
        },
        info: {
            surface: { l: 0.96, c: 0.020, h: HUE.info },
            border: { l: 0.54, c: 0.130, h: HUE.info },
            solid: { l: 0.52, c: 0.130, h: HUE.info },
            solidOn: { l: 0.99, c: 0, h: HUE.info },
            text: { l: 0.44, c: 0.100, h: HUE.info },
        },
        unknown: {
            surface: { l: 0.96, c: 0.005, h: HUE.unknown },
            border: { l: 0.55, c: 0.015, h: HUE.unknown },
            solid: { l: 0.52, c: 0.015, h: HUE.unknown },
            solidOn: { l: 0.99, c: 0, h: HUE.unknown },
            text: { l: 0.42, c: 0.015, h: HUE.unknown },
        },
    },

    primary: { l: 0.50, c: 0.20, h: HUE.primary },
    primaryOn: { l: 0.99, c: 0, h: HUE.primary },

    // Same reuse as dark: identical to critical.solid/solidOn (ADR-0008 §2).
    destructive: { l: 0.52, c: 0.20, h: HUE.critical },
    destructiveOn: { l: 0.99, c: 0, h: HUE.critical },
};

export const themes = { dark, light };

/**
 * Token setleri (eo-ux §8, Ertuğrul 2026-09-23). Bir set yalnızca görsel
 * katmandır; yerleşim, navigasyon, sayfalama ve metin setten bağımsız,
 * paylaşılan koddur (ADR-0007).
 *
 *   classic      bugünkü set, değerleri değişmez
 *   observatory  şimdilik classic'in birebir kopyası; U2 bulguları değiştirir
 *
 * Her set aynı anahtarlara sahip olmalı — validate-contrast.mjs bunu da kontrol
 * eder ve her seti ayrı ayrı doğrular.
 */
export const sets = {
    classic: themes,
    observatory: structuredClone(themes),
};
