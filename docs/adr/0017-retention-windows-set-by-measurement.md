# ADR-0017: Saklama pencereleri ölçümle yeniden belirlendi

- **Durum:** Kabul edildi
- **Tarih:** 2026-09-20
- **Karar verenler:** Ertuğrul Ünal
- **İlgili:** ADR-0012 (saklama kademelerini **kısmen geçersiz kılar**),
  ADR-0016 (PostgreSQL bağımlılığı), README ilke 1

## Bağlam

ADR-0012 üç kademeli saklamayı kurdu ve süreleri şu gerekçeyle seçti:

| Çözünürlük | Saklama | ADR-0012'nin gerekçesi |
|---|---|---|
| Ham | 2 gün | "Saat 03:14'te tam olarak ne oldu" |
| 5 dakika | 30 gün | "Bu, yamadan sonra mı başladı" |
| 1 saat | **400 gün** | "Kapasite planlama, **yıla yıl karşılaştırma**" |

Ve şunu ekledi:

> *"Depolama maliyeti her adımda yaklaşık on kat düşüyor, yani uzun kuyruk
> neredeyse bedava; diskin neredeyse tamamı ham penceresi."*

Bu, ölçüm yapılmadan yazılmıştı. 20 Eylül 2026'da canlı bir estate'e karşı
ölçüldü ve **yanlış çıktı.**

### Ölçüm

200 varlıklı estate, 6 084 seri, 30 saniyelik örnekleme:

| | Ölçülen |
|---|---|
| Örnek yazma hızı | 335 / saniye |
| `sample` satır başına | 92,2 bayt (indeks dahil) |
| `bucket` satır başına | 173,1 bayt |

### Aritmetik neden yürümüyor

Birim zaman başına maliyet gerçekten on kat düşüyor. Ama **saklama süresi**
aynı oranda artıyor ve fazlasını götürüyor:

```
Ham        2 880/gün ×   2 gün =  5 760 satır/seri
5 dakika     288/gün ×  30 gün =  8 640 satır/seri   ← ham'dan fazla
1 saat        24/gün × 400 gün =  9 600 satır/seri   ← en büyüğü
```

Yani her kademe kendinden öncekinden **daha çok** satır tutuyordu. "Diskin
neredeyse tamamı ham penceresi" ifadesi tersine dönmüştü: ham pencere üçünün
**en küçüğüydü**. Uzun kuyruk bedava değil, en pahalı kısımdı.

Toplam: **≈ 20,9 GB**, 200 varlıklı bir kurulum için. ADR-0012'nin kendi örnek
ortamı (30 host, 800 VM) bundan belirgin biçimde büyük.

## Karar

**Saatlik kademenin saklama süresi 400 günden 90 güne inecek.**

Diğer iki kademe değişmiyor: ham 2 gün, beş dakikalık 30 gün.

Sonuç:

```
Ham        2 880/gün ×  2 gün = 5 760 satır/seri
5 dakika     288/gün × 30 gün = 8 640 satır/seri
1 saat        24/gün × 90 gün = 2 160 satır/seri   ← artık en küçüğü
```

Toplam **≈ 13,6 GB** (20,9 GB'dan −%35).

## Gerekçe

Üç şey aynı yöne işaret etti:

**1. Sayı, iddiayı tutmuyordu.** ADR-0012 uzun kuyruğun bedava olduğunu
varsayarak 400 günü seçti. Bedava olmadığı ölçülünce, o seçimin dayanağı
kalmadı. Karar ölçümden önce verilmişti; ölçümden sonra yeniden verildi.

**2. 90 gün, trend için yeterli bir taban.** "Bu datastore ayda %2 büyüyor, 14
ay sonra dolar" demek için 90 günlük saatlik veri yeterlidir. Kapasite
planlamanın istediği eğim, üç aylık bir tabandan güvenle çıkarılır.

**3. Olay incelemesi bu kademeye zaten bakmıyor.** Bir olayın "tam olarak ne
oldu"su ham pencerededir (2 gün), "ne zaman başladı"sı beş dakikalıktadır
(30 gün). Saatlik kademe üçüncü bir soruya, kapasiteye hizmet eder.

## Ne kaybedildi — açıkça

**Yıla yıl karşılaştırma bitti.** ADR-0012 400 günü tam olarak bunun için
seçmişti: bu aralık 366 günü geçtiği için "bu Aralık, geçen Aralık'a göre
nasıl" sorusunu cevaplayabiliyordu. 90 günde bu soru **cevaplanamaz.**

Somut kayıplar:

- **Mevsimsellik.** "Her Aralık toplu iş yükü ikiye katlanıyor" gibi yıllık
  desenler görünmez olur. Üç aylık pencere bir yılın çeyreğidir ve mevsimi
  trend sanmaya açıktır — Kasım'da ölçülen bir eğim, Ocak'ta yanlış çıkabilir.
- **Uzun vadeli kapasite savunması.** "Geçen yıl bu ay şu kadardı" ile
  desteklenen bir donanım talebi artık ürünün verisiyle kurulamaz.

Bu bilinçli bir takas: 7,3 GB'a karşılık yıllık karşılaştırma. Mevsimsellik
gerçekten gerekirse yollar açık ve ikisi de ayrı bir karardır — saatlik
pencereyi geri uzatmak (yapılandırılabilir, kod değişmeden), ya da ADR-0012'nin
§8'de açık bıraktığı **günlük dördüncü kademeyi** eklemek. Günlük kova, seri
başına yılda 365 satırdır — 400 günlük saatlikten kırk kat ucuz ve
mevsimselliği görmeye fazlasıyla yeter.

## Değerlendirilen alternatifler

### A. 400 günde kalmak, diski kabul etmek
20,9 GB, 200 varlıklı bir kurulum için katlanılabilir. Ama ADR-0012'nin örnek
ortamı bunun birkaç katı ve rakam doğrusal büyüyor; ayrıca "neredeyse bedava"
gerekçesi artık yanlış olduğu bilinen bir cümle. Sayıyı bilerek korumak
savunulabilirdi, **bilmeden** korumak değil.

### B. 180 gün
15,7 GB. Yarım yıl, mevsimselliğin bir kısmını gösterir ama yıla yıl
karşılaştırmayı yine vermez — yani asıl kaybı önlemeden maliyetin yarısını
öder. İki ucun arasında kalmak, ikisinin de faydasını almamak oldu.

### C. Günlük dördüncü kademe eklemek
Doğru uzun vadeli cevap ve muhtemelen bir gün yapılacak. Bugün yapılmadı çünkü
yeni bir çözünürlük yeni bir katlama yolu, yeni bir su işareti ve yeni testler
demek; mevsimsellik ihtiyacı henüz somut bir talep değil. Bir sayıyı
değiştirmek ile bir kademe eklemek aynı büyüklükte iş değil.

### D. `storagePath` gecikmesini de bırakmak *yerine* saklamayı kısmak
Bunlar alternatif değil, ikisi de yapıldı: gecikme çifti aynı gün bırakıldı
(8 620 → 6 084 seri) ve saklama ondan sonra kısıldı. 29,6 GB → 20,9 GB → 13,6 GB.

## Sonuçlar

- **Mevcut kurulumlarda veri silinir.** 90 günden eski saatlik kovalar bir
  sonraki sıkıştırma geçişinde gider ve geri gelmez. Bu estate'te veri birkaç
  saatlik olduğu için bugün hiçbir şey silinmedi; bir yıllık geçmişi olan bir
  kuruluma bu ayar **275 günlük veri kaybı** olarak iner. Yükseltme notlarına
  yazılmalı.
- `Retention:HourlyDays` ile yapılandırılabilir olmayı sürdürüyor; değişen
  yalnızca varsayılan.
- ADR-0012'nin üç kademeli tasarımı, katlama sırası, beş sayılı kova ve
  yeniden toplama kuralı **aynen geçerli.** Değişen tek şey bir sayı ve onu
  seçtiren gerekçe.

## Bu kararı yeniden değerlendirmemiz gereken durum

- Mevsimsellik somut bir talep olduğunda: günlük kademe (alternatif C).
- Ölçülmüş bir kurulumda 13,6 GB bile sorun olduğunda — o noktada kısılacak
  yer saatlik değil, beş dakikalık kademedir; artık en büyük olan odur.
- Seri sayısı belirgin biçimde arttığında: rakam seriyle doğrusal büyür, ve
  bu ADR'nin tüm sayıları 6 084 seri içindir.
