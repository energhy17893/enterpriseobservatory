# ADR-0024: Yapılandırma kontrolleri alarm değil bulgudur

- **Durum:** Kabul edildi
- **Tarih:** 2026-09-22
- **Karar verenler:** Ertuğrul Ünal
- **İlgili:** ADR-0007 (alarm merkezileştirme), ADR-0013 (operatör eylemleri ve atıf),
  ADR-0021 (kör nokta datastore başına), `product-architecture.md` §2 ve §6

## Bağlam

`product-architecture.md` §2 ürünün cevapladığı dört soruyu ayırır ve ikisinin
**farklı türde** olduğunu söyler: *"sağlıklı mı"* alarmdır — koşul geçince
kendiliğinden kapanır; *"doğru kurulmuş mu"* bulgudur — aylarca doğrudur, insan
düzeltir **ya da riski kabul eder**. §6 bulgunun yaşam döngüsünü tarif eder:
açık → düzeltildi, ya da kabul edildi (kim, ne zaman, neden, **ne zamana kadar**).

M3 bu yaşam döngüsünü kurdu (`ComplianceFinding`, kabul, süreli istisna,
geçiş geçmişi, denetim izi) — ama yalnızca Broadcom SCG kontrolleri için.

M8 ise "doğru kurulmuş mu" sorusunun ikinci yarısını — *bir şey düştüğünde
ayakta kalır mı* — **alarm olarak** gönderdi. 22 Eylül 2026 yayımından sonra
canlıda ölçülen:

| Kural | Açtığı kayıt | Tür |
|---|---|---|
| HA karnesi — admission control kapalı | 3 (3/3 küme) | Warning alarm |
| HA karnesi — ağ yedekliliği uyarısı bastırılmış | 2 | Warning alarm |
| Depolama yolu — tek yol / tek HBA (ilk çalıştığında) | 12 | Warning alarm |

Bunların hiçbiri kendiliğinden kapanmaz. "Admission control kapalı" üç kümede
bilinçli bir tercih olabilir; alarm kutusunda operatörün iki seçeneği var:
sonsuza kadar görmek ya da susturmak. İkisi de §2'nin uyardığı sonuca çıkar —
kutuya bakmayı bırakmak. Ürünün "kabul ettik, sebebi şu, şu tarihe kadar" deme
yolu var, ama bu kayıtlar o yolun üzerinde değil.

Dalga 1'de aynı türden iki kontrol daha yazılacak (M8.4 bakım modu engelleri,
M8.7 bitiş tarihi radarı), M9 ise baştan sona bu türden. Karar şimdi
verilmezse her biri önce alarm olarak yazılıp sonra taşınacak.

### Mevcut motorun iki sınırı

Koddan okundu, varsayılmadı:

1. **Yalnızca host, yalnızca tek ayar.** `ComplianceEvaluation.Evaluate` canlı
   `EsxiHost` varlıklarını dolaşır; bir `SettingCheck` bir gelişmiş ayarı ya da
   bir host özelliğini okur. M8'in kontrolleri küme, cihaz ve VM hakkındadır ve
   **hesaplanır**: bir kuralı VM'in fiilen çalıştığı host'la karşılaştırır, bir
   cihazın yollarını sayar.
2. **Kimlik `(kontrol, katalog sürümü, varlık)`.** Bir host'un otuz cihazı için
   "tek yol" otuz ayrı bulgudur ve hepsi aynı üçlüye düşer.

## Karar

**Bir yapılandırma ya da süreklilik kontrolünün sonucu `ComplianceFinding`
yaşam döngüsüne yazılır; alarm kutusuna düşmez.**

Ayrım kuralı, tek soru: *koşul, kimse bir şey yapmadan geçebilir mi?*

- **Geçebilir** → alarm. CPU çekişmesi, bellek baskısı, ölü yol, HA'nın "failover
  kaynağı yetersiz" **olayı**, toplayıcıya ulaşılamıyor.
- **Geçemez; biri bir ayarı değiştirmeli ya da riski üstlenmeli** → bulgu.
  Admission control kapalı, tek HBA, DRS kural ihlali, bağlı ISO, sertifika 40
  gün sonra bitiyor, build'den sonra N güvenlik duyurusu.

Kapsam:

1. **İkinci, ürüne ait bir katalog.** SCG Broadcom'un kataloğudur ve veri olarak
   yutulur; süreklilik kontrolleri bizimdir ve kodla birlikte sürümlenir
   (`eo-continuity`, sürüm = ürünün kontrol kümesi sürümü). Her kontrolün kimliği,
   başlığı, gerekçesi ve beklenen değeri katalogdadır; ekranda ve raporda SCG
   kontrolüyle yan yana, **kaynağı ayrı** görünür.
2. **Değerlendirici varlık türünden bağımsızlaşır.** Bir kontrol, hangi varlık
   türüne baktığını ve verdiğini (geçti / kaldı / değerlendirilemedi + gözlenen
   + beklenen) söyleyen bir fonksiyondur. SCG'nin `SettingCheck`'i bunun host'a
   bakan, tek ayar okuyan özel hâli olur; davranışı değişmez.
3. **Bulgu kimliğine bir "konu" alanı eklenir**: `(kontrol, katalog sürümü,
   varlık, konu)`. Konu çoğu kontrolde boştur; "tek yol"da cihazın NAA'sı, DRS
   ihlalinde kuralın adıdır. Şema değişikliğidir.
4. **`Değerlendirilemedi` birinci sınıftır** ve M3'teki anlamıyla taşınır:
   `configurationEx` okunamayan bir küme "HA kapalı" değil "bakılamadı"dır.
5. **Mevcut alarmlar taşınır, silinmez.** Geçiş sırasında bir M8 alarmı açıksa
   karşılığı bulgu olarak açılır, alarm "bulguya taşındı" notuyla çözülür.
   Operatörün o alarma yaptığı susturma, bulguya **kabul olarak taşınmaz** —
   susturma bir gerekçe ve bitiş tarihi taşımaz, ve uydurulmuş bir kabul §6'nın
   yasakladığı süresiz istisnadır.

Kapsam dışı: eşik ve metrik kuralları (alarm kalır); `StorageLatencyBlindSpot`
(ADR-0021 — bir ölçüm sınırının bildirimi, ne alarm ne bulgu; yeniden başlatma
gürültüsü ayrı iş, D paketi); bulguların bildirimi (e-posta raporu M5'te var,
anlık bildirim ayrı karar).

## Gerekçe

- **İlke 4 — gürültü operatörün düşmanıdır.** Kendiliğinden kapanamayan bir
  kayıt alarm kutusunda kalıcı gürültüdür. M8.6 ilk çalıştığında bir anda 12
  alarm açtı; M9'un kontrolleri (Tools sürümü, eski adaptör) onlarcasını açar.
- **§6 zaten yazılı ve M3 zaten kurulu.** Kabul, süreli istisna, geçiş geçmişi,
  denetim izi ve denetçi formatında rapor var. Aynı yaşam döngüsünü alarm
  tarafında ikinci kez kurmak yerine var olanı genişletiyoruz.
- **Satılabilir fark.** "Bu küme N+1'i karşılamıyor; 14 Mart'ta Ahmet kabul
  etti, gerekçesi bütçe, 30 Haziran'a kadar" bir denetçinin ve bir yöneticinin
  okuyacağı cümledir. Susturulmuş bir alarm bunu söyleyemez.
- **Sıra.** Dalga 1 ve M9 bu türden kontroller ekliyor; karar şimdi, taşıma
  bir kez.

## Değerlendirilen alternatifler

### Alternatif A: Alarm olarak bırakmak, önem seviyesini düşürmek

`Info` seviyesinde alarm. En ucuzu; kod değişmez.

Seçilmedi çünkü sorun önem değil **ömür**. Info seviyesinde de olsa kayıt
kapanmaz, kabul edilemez ve "neden hâlâ açık" sorusuna cevap veremez. vROps'un
vSphere 5.5'e sabitlenmiş hardening alarmlarının 8.18'de hâlâ gönderilmesi tam
bu yolun sonucudur (`reference-approaches.md` §3).

### Alternatif B: Alarmlara "kabul" ve "bitiş tarihi" eklemek

Alarm yaşam döngüsünü genişletmek: alarm kabul edilebilir, kabulün süresi olur.

Seçilmedi çünkü iki yaşam döngüsünü tek tipte birleştirir ve ikisini de bozar:
bir CPU çekişmesi alarmının "30 Haziran'a kadar kabul edildi" durumu anlamsızdır,
ve koşulu geçtiğinde otomatik çözülen bir kaydın kabul geçmişi kaybolur. §2'nin
"tek mekanizmaya bindirmek ürünü ya gürültüye boğar ya sessizleştirir" uyarısı
bunun içindir.

### Alternatif C: Süreklilik kontrollerini SCG kataloğuna satır olarak eklemek

Tek katalog, tek değerlendirici; kontrolleri CSV'ye yazmak.

Seçilmedi çünkü SCG Broadcom'un belgesidir ve lisansıyla, sürümüyle birlikte
**değiştirilmeden** yutulur (M3.1). İçine kendi satırlarımızı koymak, bir
denetçiye "bu Broadcom'un kontrolüdür" dedirtir; değildir. Ayrıca SCG satırı bir
ayar adı taşır, bizim kontrollerimiz hesaplanır.

### Alternatif D: Üçüncü bir kayıt türü — "süreklilik bulgusu"

Uygunluk bulgusundan ayrı tablo, ayrı ekran, ayrı yaşam döngüsü.

Seçilmedi çünkü yaşam döngüsü **aynıdır** — açık, düzeltildi, kabul, süreli
istisna. Ayrı bir tür, aynı dört durumu ikinci kez yazdırır ve raporu ikiye
böler. Fark kaynaktadır (kimin kataloğu), türde değil; katalog alanı bunu
taşır.

## Sonuçlar

### Olumlu

- Alarm kutusu yalnızca kendiliğinden geçebilen şeyleri taşır; M9 eklendiğinde
  de öyle kalır.
- Süreklilik raporu (M8.10) ve uygunluk raporu (M5.2) aynı modelden beslenir;
  kabul ve istisna her ikisinde de görünür.
- M8.4, M8.7 ve M9 doğrudan bulgu olarak yazılır; taşıma bir kez yapılır.
- `Değerlendirilemedi` süreklilik kontrollerine de gelir: bakılamayan küme
  "sağlıklı" görünmez.

### Olumsuz / kabul ettiğimiz bedel

- **Şema değişikliği** (konu alanı, katalog kaynağı) ve bir **veri geçişi**
  (açık M8 alarmları → bulgular). Geri dönüşü olmayan yayımdır; öncesinde döküm.
- **Bulgular anlık bildirilmez.** Bugün "admission control kapandı" alarm olarak
  e-posta tetikleyebilir; bulgu olduğunda yalnızca ekranda ve zamanlanmış
  raporda görünür. Bir kontrolün *değişmesi* (dün geçiyordu, bugün kaldı) acil
  olabilir; bu ADR onu çözmüyor. **Bilinen boşluk.**
- Değerlendiriciyi genelleştirmek M3'ün çalışan koduna dokunur; SCG
  davranışının değişmediği mevcut testlerle kanıtlanmalı.
- M8'in dört kuralı ve testleri yeniden yazılır: `IAnalysisRule` +
  `AlertDefinition` yerine kontrol + verdi.
- Susturulmuş M8 alarmlarının susturması taşınmaz; operatör bulguyu yeniden
  kabul etmek zorunda kalır. Bilinçli: uydurulmuş bir gerekçe yazmaktansa
  sormak.

### Bu kararı yeniden değerlendirmemiz gereken durum

- Alıcılar bulgu **değişimlerinin** anlık bildirimini isterse — o zaman
  "bulgu geçişi → bildirim" ayrı bir ADR'dir, bulguyu alarma geri çevirmek değil.
- Bir kontrolün hem kendiliğinden geçebildiği hem kabul gerektirdiği görülürse
  (aday: sertifika bitişi — yenilenince geçer, ama yenilemek bir eylemdir;
  bu ADR onu bulgu sayıyor), ayrım kuralı yetersiz kalmış demektir.
- Konu alanı üçüncü bir boyuta ihtiyaç duyarsa (varlık + cihaz + yol), kimlik
  modeli yeniden düşünülür.
