# Okuma bütçesi — 2000 VM'lik sentetik ölçüm (T1.1, paket A)

*22 Eylül 2026. Harness: `tests/EnterpriseObservatory.Collectors.Vsphere.Tests/ReadBudgetMeasurementTests.cs`
(+ `SimulatedVcenter.cs`). Çalıştırma:*

```
dotnet test tests/EnterpriseObservatory.Collectors.Vsphere.Tests --filter "FullyQualifiedName~ReadBudgetMeasurement" --logger "console;verbosity=detailed"
```

## Canlı ölçüm — gerçek `QueryPerf` gidiş-dönüşü (22 Eylül 2026)

Aşağıdaki sentetik tabloların tek varsayımı çağrı gecikmesiydi; bu bölüm onu
ölçüyor. Probe, salt-okunur:
`dotnet run --project tools/EnterpriseObservatory.VsphereProbe -- --from-store --mask --time-queryperf 30`
(bağlantı ürünün deposundan, parola user-secrets'tan; servis durdurulmadı).
Estate: 10 host, 145 VM; `maxQueryMetrics` okunamıyor (varsayılan 256).
**Neden okunamıyor** (tek salt-okunur sorgu, `--why-max-query-metrics`):
`config.vpxd.stats.maxQueryMetrics` → **fault `InvalidName` — seçenek bu vCenter'da tanımlı değil** (b), yetki reddi değil (a).
Süre, istemci çağrısının tamamı (istek + sunucu + yanıt + ayrıştırma);
her vakada ilk çağrı ısınma sayılıp dışarıda bırakıldı; çağrılar arası 200 ms.

| vaka | koşu (n) | medyan | p95 | min | maks |
|---|---|---|---|---|---|
| (a) 12 VM × 17 sayaç, maxSample 3 | 30 | 130 ms | 270 ms | 108 | 337 |
| | 40 | 131 ms | 364 ms | 107 | 576 |
| | 20 | 143 ms | 219 ms | 110 | 250 |
| | 20 | 157 ms | 488 ms | 104 | 689 |
| (b) 1 host × 28 sayaç | 30 | 149 ms | 529 ms | 118 | 564 |
| | 40 | 141 ms | 283 ms | 114 | 598 |
| | 20 | 138 ms | 174 ms | 131 | 526 |
| | 20 | 147 ms | 180 ms | 115 | 397 |
| (c) 1 VM × 17 sayaç (ölçek kontrolü) | 30 / 40 / 20 / 20 | 34 / 34 / 33 / 32 ms | 521 / 494 / 52 / 73 ms | 29 | 880 |

Toplam **110 çağrı** (a), **110 çağrı** (b). Özet: (a) medyan **~135 ms**
(koşular arası 130–157), p95 **220–490 ms** (tipik ~320). (b) medyan **~145 ms**,
p95 170–530 ms. p95 koşudan koşuya oynuyor — n=20–40 ile kuyruk kararlı değil.

(c) ile (a)'nın farkı: 1 VM 33 ms, 12 VM ~135 ms → **~25 ms sabit + VM başına ~9 ms**.
Maliyetin çoğu varlık başına; batch'i büyütmek yalnız sabit kısmı (2000 VM'de
167 × ~25 ms ≈ 4 sn) kazandırır. Bu bir tasarım önerisi değil, ölçüm.

### 2000 VM'in sığması — gerçek gecikmeyle yeniden hesap

Bütçe: 30 sn × 0,8 = 24 sn; kaynağa 21,6 sn'de "dur" deniyor. 2000 VM = 167
`QueryPerf` + ~3 ön çağrı (katalog, limit, probe; ~0,5 sn).

| çağrı başına | 2000 VM tam okuma | 21,6 sn'ye sığan | sığıyor mu? |
|---|---|---|---|
| **135 ms (medyan)** | 167 × 0,135 + 0,5 ≈ **23,0 sn** | ~156 sorgu ≈ **1870 VM** | **Hayır, kıl payı** (~1,4 sn aşıyor); kalan ~130 VM sonraki döngüde, her VM ≤ 60 sn'de bir okunuyor |
| 157 ms (en kötü koşu medyanı) | ≈ 26,7 sn | ≈ 1600 VM | Hayır |
| ~320 ms (tipik p95, kötümser ortalama) | ≈ 54 sn | ≈ 790 VM | Hayır — her VM ~3 döngüde bir |
| başabaş | ≤ ~126 ms | — | — |

~80 host eklenirse (batch 7 × 28 sayaç → 12 sorgu, tek host 145 ms; 7'li
batch'in süresi ölçülmedi) +2–4 sn daha: **2000 VM + 80 host medyan gecikmede
de sığmıyor.** Toplam süre ortalamayı izler, medyanı değil; p95'in 2–3 katı
kuyruk ortalamayı medyanın üstüne iter, dolayısıyla gerçekçi tahmin
"23 sn'nin biraz üstü" — sığmıyor.

## Özet — bugünkü kod (önce)

| | Değer |
|---|---|
| VM sayaç sayısı | 17 |
| Başlangıç batch'i (`maxQueryMetrics` = 256) | `floor(256 × 0,8 / 17)` = **12 VM / sorgu** |
| 2000 VM'lik tam gözlem okuması | **170 çağrı** (167 `QueryPerf` + katalog + limit + probe) |
| Süre, çağrı başına 250 / 500 / 1000 ms | **42,5 / 85 / 170 sn** |
| Gözlem zaman aşımı (`SourceTimeout`, sabit) | 25 sn — aralık 30 sn |
| **Sığıyor mu?** | **Hayır, üç gecikmede de.** Okuma 25 sn'de bırakılıyor (`AbandonedReadException`); okuma ya hep ya hiç olduğu için **2000 VM'in hiçbirinin örneği saklanmıyor**, her döngüde. |
| Başabaş | 25 sn'ye 167 sorgu için çağrı başına ≤ ~150 ms gerekir; 250 ms'de ~1150 VM, 500 ms'de ~560 VM'e kadar sığar. |

**Ana bulgu:** mevcut sıralı okuma, 2000 VM'i 30 sn'lik aralığa gerçekçi hiçbir
gecikmede sığdıramıyor. Kısmi ilerleme (T1.1) bunu "hiç veri yok"tan "bütçeye
sığan kadar veri + kalanı açıkça okunmadı" durumuna çevirir; ama tamamını
sığdırmak **tek oturumda sıralı sorgu ile mümkün değil** — paralel sorgu (ADR-0005
§5 aile başına tavan, T2.3) ya da gevşek limit yorumunda daha büyük batch
gerekir. Bu, planlayıcı için ayrı bir karar.

## Varsayımlar

- **Çağrı gecikmesi** bu tablolarda varsayım (250 / 500 / 1000 ms, sabit). Gerçeği
  yukarıdaki canlı ölçümde: 12 VM'lik sorgu medyan ~135 ms — taranan en iyimser
  değerden de hızlı; sığma hesabı orada yeniden yapıldı.
- Sunucu limiti **katı** yorumla uygulanıyor (varlık × sayaç ≤ limit) — ADR-0005 §2.
- Sanal saat: çağrılar gerçek zaman harcamıyor; süre = çağrı sayısı × gecikme.
  Soru zaten çağrı dizisi üzerinde aritmetik.
- Yalnızca VM'ler (host/datastore yok). Gerçek 2000 VM'lik bir estate ~80 host
  daha ekler: +~5 sorgu (host sayaç sayısı daha büyük, batch daha küçük).
- **Envanter bu harness'ta simüle edilmedi.** Sayfa boyutu 250 ile 2000 VM ≥ 8
  `RetrieveProperties` sayfası + host/datastore/ağ sayfaları eder; 25 sn'ye
  sığması sayfa başına ≲ 2 sn gerektirir. Ölçülmedi — sayfa gecikmesi canlıda
  bilinmiyor. Envanter zaman aşımı T1.1 ile aralıktan türetildi (bkz. "Sonra").

## Senaryolar — önce

Her senaryo aynı kaynak örneğiyle iki döngü okundu (döngü 2 = öğrenilen bir şey
hatırlanıyor mu).

| senaryo | gecikme | döngü | çağrı | QueryPerf | ret | sn | örnekli VM | hatalar |
|---|---|---|---|---|---|---|---|---|
| limit okunabilir (256) | 250 ms | 1 | 170 | 167 | 0 | 42,5 | 2000* | — |
| limit okunabilir (256) | 250 ms | 2 | 170 | 167 | 0 | 42,5 | 2000* | — |
| limit okunamıyor, gerçek 64 | 250 ms | 1 | 672 | 669 | 2 | 168,0 | 2000* | batch küçültüldü |
| limit okunamıyor, gerçek 64 | 250 ms | 2 | 672 | 669 | **2** | 168,0 | 2000* | batch küçültüldü |
| vm-1 silinmiş | 250 ms | 1 | 3 | 0 | 0 | 0,8 | **0** | VirtualMachine (tüm tip) |
| vm-1000 silinmiş | 250 ms | 1 | 87 | 84 | 0 | 21,8 | **996** | VirtualMachine (tüm tip) |
| limit okunabilir (256) | 500 ms | 1 | 170 | 167 | 0 | 85,0 | 2000* | — |
| limit okunamıyor, gerçek 64 | 500 ms | 1 | 672 | 669 | 2 | 336,0 | 2000* | batch küçültüldü |
| vm-1000 silinmiş | 500 ms | 1 | 87 | 84 | 0 | 43,5 | 996* | VirtualMachine (tüm tip) |
| limit okunabilir (256) | 1000 ms | 1 | 170 | 167 | 0 | 170,0 | 2000* | — |
| limit okunamıyor, gerçek 64 | 1000 ms | 1 | 672 | 669 | 2 | 672,0 | 2000* | batch küçültüldü |

\* Kaynağın *döndürdüğü*; runner 25 sn'de okumayı bıraktığı için gerçekte
**saklanan 0**. Tam tablo harness çıktısında (500/1000 ms satırlarının döngü 2'si
döngü 1 ile aynı).

Okunanlar:

- **T1.1** — 42,5 sn'lik okuma 25 sn'lik bütçede atılıyor: 2000 VM için ürün
  hiçbir döngüde tek örnek saklamıyor. Zaman aşımı sabit (25 sn), aralıkla
  bağı yok.
- **T1.2** — gizli limitte (64) her döngü 12 → 6 → 3 diye **yeniden öğreniyor**:
  döngü başına 2 boşa sorgu. Maliyet küçük (0,5–2 sn), ama her döngü bir
  `ProtocolError` "küçültüldü" kaydı bırakıyor ve 3'lük batch 669 sorgu ediyor —
  asıl maliyet limitin kendisi.
- **T1.3** — ret mesaj metniyle tanınıyor ("restricted by administrator"). Sonra:
  metin **birincil** (Broadcom KB 301449 istemcinin aldığı metni belgeliyor:
  "Request processing is restricted by administrator"), `RestrictedByAdministrator`
  fault tipi **ikincil**; ikisinden biri yeter. Tipi hiçbir kaynak belgelemiyor ve
  canlı bir ret görülmedi — **canlıda doğrulanmadı**; limit canlıda bilerek
  tetiklenmedi.
- **T1.4** — `moRefs[0]` silinmişse probe `ManagedObjectNotFound` ile düşüyor ve
  **2000 VM'in tamamı** okunmuyor. Listenin ortasında silinmiş tek VM de o
  batch'ten sonraki **1004 VM'i** düşürüyor.

## Sonra — paket A (T1.1–T1.4)

Gözlem bütçesi artık aralıktan türüyor: 30 sn × 0,8 = **24 sn** (en az 10 sn);
runner kaynağa **21,6 sn**'de (bütçenin onda biri önce) "dur" diyor, 24 sn'de
beklemeyi bırakıyor. Envanter: 5 dk × 0,8 = **240 sn** (önce 25 sn). Harness aynı;
kaynağın token'ı sanal saatte 21,6 sn'de iptal ediliyor.

### Özet — sonra

| | Önce | Sonra |
|---|---|---|
| 2000 VM, 250 ms, bir döngüde saklanan | **0** (okuma 42,5 sn, 25 sn'de atılıyor) | **996 VM** (83 `QueryPerf`, 21,8 sn) + kalan 1004 için `Timeout` kaydı |
| 500 ms / 1000 ms | 0 / 0 | 480 / 216 VM |
| Tüm 2000 VM'in örneklenme sıklığı | hiç | 250 ms'de **her 2 döngüde bir** (60 sn), 500 ms'de ~4 döngüde bir, 1000 ms'de ~9 döngüde bir — sonraki döngü kaldığı yerden devam ediyor |
| Tam okuma sığıyor mu? | Hayır | **Hâlâ hayır** (42,5 sn > 24 sn). Sığan kadarı saklanıyor, eksik açıkça söyleniyor. |
| Gizli limit 64, döngü 2 | 2 ret, 12 → 6 → 3 yeniden | **0 ret**, 3'ten başlıyor (oturum boyunca hatırlanıyor) |
| `vm-1` silinmiş | tip düştü, **0 VM** | 1999 VM (tam okumada); probe +1 çağrı |
| `vm-1000` silinmiş | 996 VM, sonrası düştü | 1999 VM; yalıtma +6 sorgu (167 → 173) |

`QueryPerf` `maxSample` = 3 (20 sn'lik örnekler → 60 sn geçmiş) ve önceki
örnekler backfill olarak yazılıyor (T0.4a). 250 ms'de her VM 60 sn'de bir
okunduğu için **seride boşluk kalmıyor**; 500 ms ve üstünde kalıyor.

### Senaryolar — sonra, 21,6 sn'de durdurulan

| senaryo | gecikme | döngü | çağrı | QueryPerf | ret | sn | örnekli VM | hatalar |
|---|---|---|---|---|---|---|---|---|
| limit okunabilir (256) | 250 ms | 1 | 87 | 83 | 0 | 21,8 | 996 | Timeout:VirtualMachine |
| limit okunabilir (256) | 250 ms | 2 | 87 | 83 | 0 | 21,8 | 996 | Timeout:VirtualMachine |
| limit okunamıyor, gerçek 64 | 250 ms | 1 | 87 | 83 | 2 | 21,8 | 243 | Timeout, küçültüldü |
| limit okunamıyor, gerçek 64 | 250 ms | 2 | 87 | 83 | **0** | 21,8 | 249 | Timeout, küçültüldü |
| vm-1 silinmiş | 250 ms | 1 | 87 | 82 | 0 | 21,8 | 984 | Timeout; 1 VM artık yok |
| vm-1000 silinmiş | 250 ms | 2 | 87 | 83 | 0 | 21,8 | 923 | Timeout; 1 VM artık yok |
| limit okunabilir (256) | 500 ms | 1 | 44 | 40 | 0 | 22,0 | 480 | Timeout:VirtualMachine |
| limit okunamıyor, gerçek 64 | 500 ms | 2 | 44 | 40 | 0 | 22,0 | 120 | Timeout, küçültüldü |
| limit okunabilir (256) | 1000 ms | 1 | 22 | 18 | 0 | 22,0 | 216 | Timeout:VirtualMachine |
| limit okunamıyor, gerçek 64 | 1000 ms | 2 | 22 | 18 | 0 | 22,0 | 54 | Timeout, küçültüldü |

("sn" 21,6'yı biraz aşıyor: iptal, uçuştaki çağrının sonunda görülüyor ve o
çağrı kayboluyor.)

### Sınırsız okuma — sonra (tam okumanın maliyeti)

| senaryo | 250 ms | 500 ms | 1000 ms | QueryPerf | örnekli VM |
|---|---|---|---|---|---|
| limit okunabilir (256) | 42,5 sn | 85 sn | 170 sn | 167 | 2000 |
| limit okunamıyor, gerçek 64 — döngü 1 / 2 | 168 / 167,5 sn | 336 / 335 sn | 672 / 670 sn | 669 / 667 | 2000 |
| vm-1 silinmiş | 42,8 sn | 85,5 sn | 171 sn | 167 | 1999 |
| vm-1000 silinmiş | 44 sn | 88 sn | 176 sn | 173 | 1999 |

### Planlayıcı için

- **2000 VM tek oturumda sıralı sorguyla 30 sn'lik aralığa sığmıyor**; ancak
  çağrı başına ≤ ~130 ms'de sığar (24 sn'lik bütçe). Paket A bunu "hiç veri
  yok"tan "her VM 1–9 döngüde bir, eksik açıkça söyleniyor"a çevirdi. Tamamını
  her döngüde okumak ya **paralel `QueryPerf`** (T2.3'ün aile başına tavanı
  içinde), ya da limit gevşek yorumlanıyorsa **daha büyük batch** ister — ikisi
  de ölçülmeden karar verilmemeli.
- **Canlı gecikme ölçüldü** (yukarıda): medyan ~135 ms'de 2000 VM ≈ 23 sn,
  21,6 sn'lik bütçeyi ~1,4 sn aşıyor; hostlarla birlikte daha çok. Sıralı
  tasarımın sınırı ≈ 1850 VM (medyan), ≈ 800 VM (p95-kötümser).
- **T1.4, araştırma sonrası (OTel vcenterreceiver kuralı):** `ManagedObjectNotFound`
  → nesne yarılayarak yalıtılıp atlanıyor; **başka her fault** → yalnız o batch
  varlık başına tek sorguyla yeniden soruluyor, hata o varlığa yazılıyor, diğer
  batch'ler etkilenmiyor. Tipin tamamını kapsayan bir fault varlık sayısı kadar
  sorgu eder; bunu okuma bütçesi sınırlıyor, rapor tür başına bir kez.
  Yukarıdaki tablolar bu değişiklikten önce üretildi; senaryolarda başka fault
  olmadığı için sayılar aynı kalıyor.
- Envanter bütçesi 25 → 240 sn oldu; envanter okuması bu harness'ta
  simüle edilmedi ve kısmi envanter **bilerek** eklenmedi (yarım envanter
  varlıkların "kaybolması" demek, T0.1'in tersi).
