# UX kritik turu 2 (U3) — Entities, EntityDetail, Connections, Collectors

24 Eylül 2026, eropsfable. Kaynak: `web/src/routes/{Entities,EntityDetail,Connections,Collectors}.tsx`
main a09901c'de; kurallar `.claude/skills/eo-ux/SKILL.md` (§ numaraları oradan).
Ekran görüntüsü yok (ui-verification-limit); her bulgu koddan, dosya:satır ile.
Tur 1 (U2) gibi: her bulgu küçük PR, PR gövdesi hangi kuralı uyguladığını söyler,
görsel doğrulama Ertuğrul'un.

## Bulgular

| # | Ekran | Bulgu | Kural | Dosya:satır | Öncelik |
|---|---|---|---|---|---|
| CL1 | Collectors | Rol etiketi `role === 'Inventory' ? 'inventory' : 'metrics'`; API `CollectorRole.ToString()` gönderir ve enum'da **Events** ve **Configuration** (#179) da var → ikisi "metrics" diye görünür. Web tipi `role: 'Inventory' \| 'Observation'` da eksik. | §4 (yanlış iddia), ADR-0009 (roller ayrı ayrı bozulur) | `Collectors.tsx:52–54`, `api/types.ts:237`, `CollectionPorts.cs:456–476` | **hata** |
| CN5 | Connections (+Email, ScheduledReports, Setup) | `text-healthy-on`, `text-critical-on`, `text-warning-on`, `border-healthy`, `border-critical` sınıfları **token değil** (tokens.css'te yok) → test sonucu kutusu, hata notu ve "untrusted certificate" uyarısı renksiz ve çerçevesiz basılır. Durum yalnız metinle taşınıyor; başarı/başarısızlık kutusu görsel olarak aynı. | ADR-0008 §1/§4 (`ramp()` dışında renk yok), §3 (renk+şekil+metin) | `Connections.tsx:244, 358, 365, 379, 608–611, 621`; `Email.tsx:181, 213, 220`; `ScheduledReports.tsx:154, 316`; `Setup.tsx:224` | yüksek |
| EX1 | Entities | `limit: 500` sabit, "500 of 1,100" yazar ve kalan 600 VM'e ulaşılamaz; filtre var, sayfa yok. Kibar ölçüsü 1.100 VM. | §7 (gerçek sayfalama, sonsuz kaydırma yok; sayfa boyu ölçülür) | `Entities.tsx:38, 55–59` | yüksek |
| EX2 | Entities | Tek boş durum mesajı ("No entity matches. If nothing has been collected yet…"); filtre/arama aktifken "süzüldü → filtreyi temizle", hiç toplanmamışsa "Collectors'a git" ayrı olmalı. | §6 / §11.8 (üç ayrı boş durum, her biri eylemli) | `Entities.tsx:98–99` | orta |
| EX3 | Entities | Alerts sütunu 0 için boş string basar (`entity.alertCount \|\| ''`). | §6 / §11.7 (boş hücre asla boş; `-`) | `Entities.tsx:182` | düşük |
| EX4 | Entities | Kind filtresi ve Kind sütunu ham enum adı gösterir (`EsxiHost`, `VirtualMachine`). Etiket: "ESXi host", "Virtual machine", "Datastore", "Cluster"; sütunda `Identifier` yerine düz metin (kimlik değil, sözcük). | §1 (Identifier yalnız kimlik/ID için), §6 yazı | `Entities.tsx:10, 73, 157` | düşük |
| ED8 | EntityDetail | "Connections" bölümü ilişkileri listeler; aynı ad Configure → Connections (yönetim uçları) ekranında. İki farklı şey tek sözcük. Ad: "Relationships" (ADR-0004/0007 §4 dilinde). | ADR-0007 §4, §6 yazı | `EntityDetail.tsx:172` | orta |
| ED5 | EntityDetail + Entities | "Stale" üç ayrı biçimde: Entities'te `StatusBadge Unknown "stale"` + "since …" satırı, EntityDetail başlığında `StatusBadge Unknown "stale since …"`, HA bulgularında düz `(stale)` metni. Tek biçim: StatusBadge Unknown + since. | §4 (bayat açıkça, tek dil), §11.3 | `Entities.tsx:161–173`, `EntityDetail.tsx:81–85, 340` | orta |
| ED3 | EntityDetail | Identity işaretleri ve açıklama değerleri çıplak `font-mono text-xs` span; `Identifier` var. | §1 (Identifier'ı yeniden kullan) | `EntityDetail.tsx:207, 518` | düşük |
| ED4 | EntityDetail | Boş değer `—` (em dash); skill `-` der; Compliance/raporlar da `—` kullanıyor (27 yer). Karar: tek karakter, tek yerde sabit (`web/src/lib/ui.ts`), sonra toplu değişim. | §6 | `EntityDetail.tsx:242, 279–292, 343, 430` | düşük (ayrı küçük PR, tüm web) |
| CN1 | Connections | "Remove" onaysız; DatabaseCard'daki "Rotate" `window.confirm` ile. Bağlantı silmek o kaynağın varlık/alarm/bulgularını yetim bırakır; en az rotate kadar sürtünme. Onay metni sonucu söyler ("N entities from this connection will stop being observed"). | §11.9 (yıkıcı eyleme sürtünme) | `Connections.tsx:399–409` | yüksek |
| CN2 | Connections | Bayat metin ve varsayılanlar: "There is no collector for Redfish/SimpliVity yet" (M6.1 ve S3 canlıda); `isEnabled` yalnız vsphere için true; Name ipucu "from this vCenter" (iLO/SimpliVity için yanlış → "from this connection"); `KIND_LABELS` yorumu "before either has a collector". Adres placeholder `https://ovc-or-vcenter/` → SimpliVity için OVC ya da MVA. | §6 yazı (doğru iddia) | `Connections.tsx:17–22, 51–55, 503, 595–601` | orta |
| CL2 | Collectors | Kaynak kartında bağlantıya (Configure → Connections) ve varlık listesine (Entities?source=) bağlantı yok; "kimin alarmı" sorusu için iki ekran arası elle geçiş. | ADR-0007 §4 (varlık merkezli gezinme) | `Collectors.tsx:44` | düşük (U4 wallboard'dan önce değil) |

## Uygun bulunanlar (değişmez)

- Entities: sanallaştırılmış liste, donmuş başlık (scroll dışı), sayısal sütun sağa yaslı, gri satırın "neden" ikinci satırı (K3), `Include vanished` ayrı.
- EntityDetail: alarmlar gelen kutusunun aynı örnekleri ve `AlertActions` (ADR-0007 §5); ilişki cümleleri yönlü; HA karnesi okunmamış satırı gösterir; bulgularda `basis:`; SimpliVity şeridi başlığın altında (§11.6), `CarriedForward` ile.
- Connections: parola yazma-yalnız, Test → Save sırası, yapılandırmadan gelen bağlantı düzenlenemez, tür başına gruplama, DB kartı parola göstermez.
- Collectors: rol ayrı ayrı, kısmi başarısızlıkların tamamı listelenir, kapsam paneli tamamlananları da gösterir.

## PR planı (eROps'a brief sırası)

1. **CL1** — rol etiketi: `Inventory | Observation | Events | Configuration` tipi ve dört etiket ("inventory", "metrics", "events", "configuration"). Bitti: Collectors sayfasında KBVc01 için dört satır dört farklı etiketle (API `/api/collectors` ile doğrulanır).
2. **CN5** — ölü sınıflar → `ramp('Healthy'|'Critical'|'Warning').text/.border` dört dosyada; `grep -rn "healthy-on\|critical-on\|warning-on\|border-healthy\|border-critical" web/src` = 0. Kontrast kapısı zaten bu token'ları doğruluyor. Token seti değişmez (paylaşılan kod).
3. **CN1 + CN2 + ED8** — onay, metin, bölüm adı. Bitti: metinler; `Remove` onaydan geçer.
4. **EX1 + EX2 + EX3 + EX4** — sayfalama (API `offset` var mı bakılır; yoksa eklenir), üç boş durum, `-`, etiketler. Sayfa boyu: ölçülmeden yazılmaz — Kibar'da 1.100 satırlık VM listesinin sanal render süresi ölçülür, sonuç `docs/reference-approaches.md` §11'e ve teste girer.
5. **ED5 + ED3** — tek stale biçimi, Identifier.
6. **ED4** — tek boş-değer sabiti, tüm web (ayrı).

CL2 ve wallboard (U4) sonraya.
