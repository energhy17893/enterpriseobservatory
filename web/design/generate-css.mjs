/**
 * tokens.mjs -> CSS değişkenleri.
 *
 * CSS elle yazılmaz, üretilir. Gerekçe: önceki üründe 6.101 satırlık site.css
 * içinde aynı token birden fazla yerde farklı değerle tanımlanmıştı ve hangisinin
 * doğru olduğu belirsizdi. Tek kaynak + üretim bunu imkânsız kılar.
 *
 * Çalıştır:  node web/design/generate-css.mjs > web/src/styles/tokens.css
 *
 * shadcn theming kuralı (ui-ux-pro-max, Severity: High):
 *   - Semantik OKLCH değişkenleri :root VE .dark altında EKSİKSİZ tanımlanır
 *   - Tailwind v4'e @theme inline ile açılır
 *   - Bileşende ham palet rengi kullanılmaz
 */

import { themes } from './tokens.mjs';

const STATUSES = ['healthy', 'warning', 'critical', 'info', 'unknown'];
const ROLES = ['surface', 'border', 'solid', 'solidOn', 'text'];

/** OKLCH nesnesini CSS oklch() fonksiyonuna çevirir. */
const css = ({ l, c, h }) => `oklch(${+l.toFixed(4)} ${+c.toFixed(4)} ${h})`;

/** camelCase -> kebab-case */
const kebab = (s) => s.replace(/[A-Z]/g, (m) => '-' + m.toLowerCase());

function emitTheme(theme) {
    const lines = [];

    lines.push(`  --page: ${css(theme.page)};`);
    lines.push(`  --card: ${css(theme.card)};`);
    lines.push(`  --foreground: ${css(theme.foreground)};`);
    lines.push(`  --muted-foreground: ${css(theme.mutedForeground)};`);
    lines.push(`  --border: ${css(theme.border)};`);
    lines.push(`  --focus-ring: ${css(theme.focusRing)};`);
    lines.push(`  --primary: ${css(theme.primary)};`);
    lines.push(`  --primary-on: ${css(theme.primaryOn)};`);
    lines.push('');

    for (const s of STATUSES) {
        for (const r of ROLES) {
            lines.push(`  --status-${s}-${kebab(r)}: ${css(theme.status[s][r])};`);
        }
        lines.push('');
    }

    return lines.join('\n').trimEnd();
}

function emitThemeInline() {
    const lines = [];
    lines.push('  --color-page: var(--page);');
    lines.push('  --color-card: var(--card);');
    lines.push('  --color-foreground: var(--foreground);');
    lines.push('  --color-muted-foreground: var(--muted-foreground);');
    lines.push('  --color-border: var(--border);');
    lines.push('  --color-focus-ring: var(--focus-ring);');
    lines.push('  --color-primary: var(--primary);');
    lines.push('  --color-primary-on: var(--primary-on);');
    lines.push('');
    for (const s of STATUSES) {
        for (const r of ROLES) {
            const name = `status-${s}-${kebab(r)}`;
            lines.push(`  --color-${name}: var(--${name});`);
        }
        lines.push('');
    }
    return lines.join('\n').trimEnd();
}

const out = `/* ÜRETİLMİŞ DOSYA — ELLE DÜZENLEMEYİN.
 * Kaynak: web/design/tokens.mjs
 * Üret:   node web/design/generate-css.mjs > web/src/styles/tokens.css
 * Doğrula: node web/design/validate-contrast.mjs
 */

/* Koyu tema birincildir (NOC ortamı). Açık tema tam desteklidir. */
:root {
${emitTheme(themes.dark)}
}

.light {
${emitTheme(themes.light)}
}

@theme inline {
${emitThemeInline()}
}
`;

process.stdout.write(out);
