# UX kritik turu U5 — Posture ve SimpliVity sayfaları

24 Eylül 2026, eropsfable. Kaynak: `web/src/routes/Compliance.tsx`, `web/src/routes/Simplivity.tsx`
main 59dae40'ta; kurallar `.claude/skills/eo-ux/SKILL.md`. Canlı kontroller MCP
araçlarıyla (eo_list_alerts, eo_get_compliance). Ekran görüntüsü yok.

## Bulgular

| # | Ekran | Bulgu | Kural | Dosya:satır | Öncelik |
|---|---|---|---|---|---|
| SV9 | SimpliVity | "Open alerts from KBSVT" bölümü `api.alerts({source: instanceId})` ile süzüyor. Canlı: S1 olay türevli SimpliVity alarmlarının `source` alanı **"platform"**, `category` "SimpliVity" (ör. "SimpliVity VM data access not optimized", entity ALRFM01); `source=KibarHolding-KBSVT` sorgusu 0 döndü. Sayfa, SimpliVity'ye ait alarmların çoğunu göstermiyor; başlık yanlış iddia. Süzgeç: `category=SimpliVity` **veya** `source=instanceId`; başlık "SimpliVity alerts". | ADR-0007 §5 (alarm tek yerde, süzülmüş), §6 (doğru iddia), ADR-0027 kural 6 | `Simplivity.tsx:539–548, 561` | **yüksek** |
| PO4 | Posture | Bir kontrolün bulguları (`api.complianceFindings({control})`) sayfasız geliyor ve Failing+NotEvaluated satırlarının tamamı basılıyor; `maint-consolidation` gibi kontrollerde 1.100 satır. "passed" açılınca 1.100 Passing satırı daha. | §7 (sayfalama; >100 satır sanallaştırılır), §1 | `Compliance.tsx:482–543` | yüksek |
| PO2 | Posture | Bayat metinleri vCenter'a bağlı: ":278 their vCenter did not answer", ":573 Stale — vCenter did not answer". #190/#192 ile bayatlık artık okunan veri kaynağına göre (SimpliVity sessizse backup-freshness bayat). Metin kaynağı adlandırmalı ya da "its source". Brief 5'in ortak StaleBadge'i buraya da uygulanır. | §4 (bayat açıkça, doğru sebep) | `Compliance.tsx:276–282, 572–574, 596` | orta |
| PO10 | Posture | "host" sözcüğü VM/küme kontrollerinde yanlış: ":509 No hosts have been evaluated", ":502 Except every host", ":633 Except this host", ":799 every host". Kontrolün `appliesTo` türü varsa ona göre, yoksa "entity/entities". | §6 (doğru iddia) | `Compliance.tsx:502, 509, 633, 799` | orta |
| PO5 | Posture | Exceptions → "Remove" onaysız; kaldırınca bulgular yeniden Failing sayılır ve karne değişir. Rotate/Remove connection emsali (`window.confirm`). "Accept" boş gerekçeyle gönderilebiliyor; sunucu reddediyorsa hata satırı var, reddetmiyorsa gerekçesiz kabul mümkün — API'de kontrol edilir. | §11.9 (susturucu/yıkıcı eyleme sürtünme) | `Compliance.tsx:816–824, 600–616` | orta |
| PO3 | Posture | Bulgu rozeti Failing = **Warning** rengi (:21), karne "Failing remain" = **Critical** (:62). Aynı durum iki renk. Karar: bulgular alarm değil (ADR-0024) → karne de Warning; Critical yalnız alarm içindir. | ADR-0008 §1/§3 (tek durum tek rampa) | `Compliance.tsx:20–26, 57–65` | düşük |
| SV1 | SimpliVity | `PAGE_SIZE = 50` "to be measured" yorumu (:18) eskidi; `usePage` (:324–354) ve `SourceAlerts` (:562–590) iki yerel sayfalayıcı — #193'ün ortak `Pager`'ı kullanılmalı. | §7, components.md (Pager) | `Simplivity.tsx:18–19, 324–354, 562–590` | orta |
| SV6 | SimpliVity | `counter()` (:127–134) iyi-olmayan her değeri **Warning** sayıyor: FAULTY host ya da DEFUNCT VM olsa metrik kartı sarı kalır; §10.8'de FAULTY/DEFUNCT kırmızı. Durum = anahtarların `simplivityStatus` en kötüsü. | §11.3, referans §10.8 | `Simplivity.tsx:127–134` | orta |
| SV2 | SimpliVity | `dash()`/`yesNo()` kendi '-' sabitleri; brief 5'in `EMPTY` sabiti buraya da. Küme satırında "Virtual controller" hücresi '-' (:288) "uygulanmaz" anlamında; §11.7 `[z]` ayrımı — en azından `title="not applicable"`. | §6 / §11.7 | `Simplivity.tsx:75–81, 288, 318` | düşük |
| SV4 | SimpliVity | Hardware tablosu sayfasız; 26 host'ta sorun yok, 100+ host'ta §1 sınırı. Not, şimdi değişmez. | §1 | `Simplivity.tsx:471–536` | düşük (not) |

## Uygun bulunanlar

- Posture: katalog başına karne, NotEvaluated her zaman görünür, kapsam NotEvaluated'ı paydaya almıyor, `basis:` etiketi, istisna kapsamı açık ve dar varsayılan (K3 §5), `aria-pressed`/`aria-expanded`, ErrorLine token rampasıyla.
- SimpliVity: HPE sözcükleri durum olarak, null = Unknown, `CarriedForward` işareti, §11.6 durum şeridi, tablolar donmuş başlık ve sağa yaslı sayılar, alarmlar gelen kutusunun örnekleri (`AlertRow`, `BulkBar`), boş durumlar "raporluyor / henüz okunmadı" ayrımıyla.

## PR planı

1. **SV9** — SimpliVity alarm süzgeci (category **veya** source). Bitti: canlıda sayfa "SimpliVity VM data access not optimized" alarmını gösterir (bugün 1 açık, entity ALRFM01), `eo_list_alerts category=SimpliVity` sayısıyla eşit.
2. **PO4 + SV1** — Posture bulgu listesine ve SimpliVity'ye ortak `Pager` (istemci tarafı, sayfa 100; sunucu zaten tüm bulguları gönderiyor — `/api/compliance/findings`'e offset/limit eklemek ayrı karar, ölçüm: en büyük kontrol 1.100 satır × ~400 B ≈ 440 KB, kabul edilebilir). SV1'in eski yorumu gider.
3. **PO2 + PO10 + PO5** — metinler ve onay.
4. **SV6 + SV2 + PO3** — durum hesapları ve sabitler.

SV4 not olarak kalır.
