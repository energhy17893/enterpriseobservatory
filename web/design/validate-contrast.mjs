/**
 * Kontrast doğrulayıcı — CI kapısı.
 *
 * Önceki üründe durum renkleri açık temada 2.06:1'e kadar düşüyordu ve bunu
 * kimse fark etmedi, çünkü kontrolü yapan bir mekanizma yoktu. ADR-0001'deki
 * ilke burada da geçerli: insan disiplini gevşer, makine kontrolü gevşemez.
 *
 * Çalıştır:  node web/design/validate-contrast.mjs
 * Çıkış kodu 1 ise bir çift eşiği geçemiyor; build kırılır.
 */

import { themes } from './tokens.mjs';

// ---------------------------------------------------------------------------
// OKLCH -> sRGB. WCAG kontrastı sRGB tabanlıdır, bu yüzden dönüşüm şart.
// ---------------------------------------------------------------------------

function oklchToLinearSrgb({ l, c, h }) {
    const hRad = (h * Math.PI) / 180;
    const a = c * Math.cos(hRad);
    const b = c * Math.sin(hRad);

    const l_ = l + 0.3963377774 * a + 0.2158037573 * b;
    const m_ = l - 0.1055613458 * a - 0.0638541728 * b;
    const s_ = l - 0.0894841775 * a - 1.2914855480 * b;

    const L = l_ * l_ * l_;
    const M = m_ * m_ * m_;
    const S = s_ * s_ * s_;

    return {
        r: +4.0767416621 * L - 3.3077115913 * M + 0.2309699292 * S,
        g: -1.2684380046 * L + 2.6097574011 * M - 0.3413193965 * S,
        b: -0.0041960863 * L - 0.7034186147 * M + 1.7076147010 * S,
    };
}

function linearToSrgbChannel(x) {
    const v = x <= 0.0031308 ? 12.92 * x : 1.055 * Math.pow(x, 1 / 2.4) - 0.055;
    return Math.min(1, Math.max(0, v));
}

/** Gamut dışı mı? Ekranda görünecek renk tanımladığımızdan emin olmalıyız. */
function isOutOfGamut(lin) {
    const eps = 1e-4;
    return [lin.r, lin.g, lin.b].some((v) => v < -eps || v > 1 + eps);
}

function relativeLuminance(oklch) {
    const lin = oklchToLinearSrgb(oklch);
    // Kırpılmış lineer değerler üzerinden luminans (ekranda görülen renk)
    const r = Math.min(1, Math.max(0, lin.r));
    const g = Math.min(1, Math.max(0, lin.g));
    const b = Math.min(1, Math.max(0, lin.b));
    return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}

function contrast(a, b) {
    const la = relativeLuminance(a);
    const lb = relativeLuminance(b);
    const hi = Math.max(la, lb);
    const lo = Math.min(la, lb);
    return (hi + 0.05) / (lo + 0.05);
}

function toHex(oklch) {
    const lin = oklchToLinearSrgb(oklch);
    const to255 = (x) => Math.round(linearToSrgbChannel(x) * 255);
    return (
        '#' +
        [to255(lin.r), to255(lin.g), to255(lin.b)]
            .map((v) => v.toString(16).padStart(2, '0'))
            .join('')
    );
}

// ---------------------------------------------------------------------------
// Kurallar
// ---------------------------------------------------------------------------

const STATUSES = ['healthy', 'warning', 'critical', 'info', 'unknown'];

/** @returns {{label:string, fg:object, bg:object, min:number}[]} */
function rulesFor(theme) {
    const rules = [];

    rules.push(
        { label: 'foreground / page', fg: theme.foreground, bg: theme.page, min: 4.5 },
        { label: 'foreground / card', fg: theme.foreground, bg: theme.card, min: 4.5 },
        { label: 'mutedForeground / card', fg: theme.mutedForeground, bg: theme.card, min: 4.5 },
        // WCAG 2.2 AA 1.4.11 — odak göstergesi metin değil, 3:1 yeter.
        { label: 'focusRing / page', fg: theme.focusRing, bg: theme.page, min: 3.0 },
        { label: 'focusRing / card', fg: theme.focusRing, bg: theme.card, min: 3.0 },
        { label: 'border / card', fg: theme.border, bg: theme.card, min: 1.3 },
        { label: 'primaryOn / primary', fg: theme.primaryOn, bg: theme.primary, min: 4.5 },
        { label: 'primary / page (non-text)', fg: theme.primary, bg: theme.page, min: 3.0 },
    );

    for (const s of STATUSES) {
        const st = theme.status[s];
        rules.push(
            // pill metni kendi yumuşak zemininde okunmalı
            { label: `${s}: text / surface`, fg: st.text, bg: st.surface, min: 4.5 },
            // pill sayfa üzerinde de durabilir
            { label: `${s}: text / card`, fg: st.text, bg: theme.card, min: 4.5 },
            // nokta, kenarlık, bar dolgusu — metin değil
            { label: `${s}: border / card`, fg: st.border, bg: theme.card, min: 3.0 },
            { label: `${s}: border / page`, fg: st.border, bg: theme.page, min: 3.0 },
            // yüksek vurgulu rozet
            { label: `${s}: solidOn / solid`, fg: st.solidOn, bg: st.solid, min: 4.5 },
        );
    }

    return rules;
}

// ---------------------------------------------------------------------------
// Çalıştır
// ---------------------------------------------------------------------------

let failures = 0;
let gamutFailures = 0;

for (const [themeName, theme] of Object.entries(themes)) {
    console.log(`\n=== ${themeName.toUpperCase()} ===`);

    // Gamut kontrolü: tanımladığımız her renk sRGB'de gösterilebilmeli
    const walk = (obj, path = []) => {
        for (const [k, v] of Object.entries(obj)) {
            if (v && typeof v === 'object' && 'l' in v && 'c' in v && 'h' in v) {
                if (isOutOfGamut(oklchToLinearSrgb(v))) {
                    console.log(`  GAMUT DISI  ${[...path, k].join('.')}  ${JSON.stringify(v)}`);
                    gamutFailures++;
                }
            } else if (v && typeof v === 'object') {
                walk(v, [...path, k]);
            }
        }
    };
    walk(theme);

    for (const rule of rulesFor(theme)) {
        const ratio = contrast(rule.fg, rule.bg);
        const ok = ratio >= rule.min;
        if (!ok) failures++;
        const mark = ok ? 'PASS' : 'FAIL';
        console.log(
            `  ${mark}  ${ratio.toFixed(2).padStart(5)}:1  (>= ${rule.min})  ${rule.label}` +
            `   ${toHex(rule.fg)} on ${toHex(rule.bg)}`,
        );
    }
}

console.log('');
if (gamutFailures > 0) {
    console.log(`${gamutFailures} renk sRGB gamut disinda.`);
}
if (failures > 0 || gamutFailures > 0) {
    console.log(`BASARISIZ: ${failures} kontrast ihlali, ${gamutFailures} gamut ihlali.`);
    process.exit(1);
}
console.log('Tum kontrast ve gamut kurallari gecti.');
