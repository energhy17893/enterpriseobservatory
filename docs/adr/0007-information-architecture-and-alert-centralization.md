# ADR-0007: Bilgi mimarisi ve alarm merkezileştirme

- **Durum:** Kabul edildi
- **Tarih:** 2026-09-18
- **Karar verenler:** Ertuğrul Ünal
- **İlgili:** ADR-0003 (varlık modeli), ADR-0004 (ilişki taksonomisi), ADR-0006 (web stack)

## Bağlam

Önceki üründe yaklaşık 60 sayfa vardı ve gruplama **satıcıya** göreydi:
vSphere, iLO, iDRAC, SimpliVity, OneView, Storage, NSX. Bu, implementasyonun
haritasıdır — operatörün işinin değil.

Operatör sabaha karşı ekrana baktığında "iLO sayfasına mı gitsem iDRAC'e mi"
diye düşünmez. Şunu düşünür: *ne yanıyor, neden yanıyor, ne yapmalıyım,
yöneticime ne diyeceğim.*

Daha ciddi bir sorun, sayfaların **farklı veri kaynaklarından** okumasıydı:
`Alerts` sayfası `AlertLifecycleService.GetInstances()` okurken, iLO ve iDRAC
sayfaları snapshot'taki anlık alarm listesini okuyordu. Sonuç: aynı alarm iki
yerde farklı davranabiliyordu — birinde Acknowledged görünüp diğerinde açık
kalabiliyordu.

Bu ADR'nin çözdüğü asıl problem sayfa sayısı değil, **durum tekrarı**.

## Karar

### 1. Yönetici kural: çok görünüm, tek model

> Kaç ekran olursa olsun, hepsi tek modelin projeksiyonudur. Hiçbir görünüm
> kendi durumunu tutmaz, kendi alarm listesini üretmez, kendi sağlık hesabını
> yapmaz.

Önceki ürünün hatası iLO sayfasına sahip olmak değildi; iLO sayfasının farklı
veri okumasıydı. Uzmanlaşmış ekran sayısı serbesttir, **çift durum yasaktır.**

### 2. Üst seviye gruplama: operatör niyetine göre

| Grup | Operatörün sorusu |
|---|---|
| **Triyaj** | Şu an ne beni ilgilendiriyor? |
| **İnceleme** | Bu sorun nereden geliyor? |
| **Analiz** | Nereye gidiyoruz? |
| **Kanıt** | Bunu nasıl ispatlarım? |
| **Yapılandırma** | Sistemi nasıl kurarım? |

Satıcı adı (vSphere, iLO, Dell) üst seviye gruplama ekseni **değildir**; bir
filtre ve derin görünüm ekseni olarak İnceleme grubunun altında yaşar.

### 3. İnceleme grubu üç kademeli açılır

Tek bir jenerik varlık gezgini yeterli **değildir**: bir iLO düğümünün
PSU / fan / DIMM / termal / IML detayı genelleşmez, MPIO yol matrisi de öyle.
Hepsini tek tabloya sıkıştırmak ürünün alan zenginliğini kaybettirir.

| Kademe | Kapsam | İçerik |
|---|---|---|
| **1. Varlık gezgini** | Türler arası | Bulma, filtreleme, karşılaştırma. Tek tip sütunlar: ad, tür, sağlık, alarm sayısı, son görülme. |
| **2. Varlık detayı** | Tek varlık | İlişkilerde gezinme, gözlemler, o varlığa kapsamlanmış alarmlar. Türe göre render edilen bölümler. |
| **3. Derin görünüm** | Alan uzmanı araç | iLO donanım paneli, MPIO yol matrisi, fabric radar, SEL/IML günlüğü. Ayrı alt sayfa ve alt menü. |

#### Navigasyon eşlemesi

Satıcı adı menüde **iki farklı şey** açabilir ve ikisi de sağlanır:

| Menü öğesi | Açtığı şey | Kademe |
|---|---|---|
| İnceleme → Sunucular / VM'ler / Datastore'lar | Varlık gezgini, o türe filtrelenmiş | 1 |
| İnceleme → iLO / iDRAC / SimpliVity | Varlık gezgini, o kaynağa filtrelenmiş **kayıtlı görünüm** | 1 |
| ⌞ alt menü → iLO Donanım Paneli | Uzmanlaşmış derin görünüm | 3 |
| ⌞ alt menü → MPIO Yol Matrisi | Uzmanlaşmış derin görünüm | 3 |
| ⌞ alt menü → Fabric Radar | Uzmanlaşmış derin görünüm | 3 |

Yani "iLO" tıklaması operatörü tanıdık bir listeye götürür (kayıtlı filtre),
oradan alt menü veya bir satıra tıklayarak derinleşir. Kas hafızası korunur,
derinlik kaybolmaz, arkada tek model vardır.

Kademe 3 ekranlarına alt menüden **doğrudan** da erişilir. Hepsi kademe 1 ve 2
ile aynı varlıkları, aynı alarm durumunu okur.

### 4. Varlık merkezli gezinme

ADR-0004'teki ilişki taksonomisi sayesinde operatör bir host'tan HBA'sına,
oradan switch portuna, oradan array'e **tıklayarak** gidebilir. Ürünün "uçtan
uca triangülasyon" vaadi ilk kez gezinilebilir hale gelir.

Önceki üründe bu zincir ayrı sayfalara dağılmıştı ve kullanıcı zihninde
birleştirmek zorundaydı.

### 5. Alarm merkezileştirme

**Alarm Gelen Kutusu** birincil triyaj yüzeyidir. Varlık sayfaları o varlığa
kapsamlanmış alarmları gösterir — **aynı instance'lar, aynı aksiyonlar, aynı
durum**. Ayrı bir liste yoktur.

#### 5.1 Olay gruplaması

Varsayılan görünüm olay bazlıdır; ham alarm listesi olay grubunun altında
açılır. Üç kural gruplamanın alarm gizlemesini engeller:

1. **Grup başlığı gerçek sayıyı gösterir** — "SFP arızası · 12 alarm · 8 varlık".
   Operatör neyin katlandığını görmeden katlanmış olanı kabul etmez.
2. **Düz liste her zaman bir tık uzakta** — görünüm anahtarı `Olaylar` /
   `Tüm alarmlar`.
3. **Hiçbir alarm yalnızca grup içinde yaşamaz** — her alarm düz listede de
   sayılır ve bulunur.

#### 5.2 Korelasyonun iki sınıfı

Bu ayrım yapılmazsa README ilke 1 ("asla uydurma") çiğnenir.

| Sınıf | Nasıl kurulur | Varsayılan davranış |
|---|---|---|
| **Topolojik** (yüksek güven) | Graf yolu ispatlıyor: SFP → `ConnectedTo` → HBA → host → `RunsOn` → şu VM'ler | Katlanır. Gerçek bir olaydır. |
| **Zamansal** (düşük güven) | Yalnızca "30 saniye içinde 12 alarm çıktı" | **Katlanmaz.** "Muhtemelen ilişkili" rozetiyle önerilir; operatör isterse gruplar. |

Sistem yalnızca **ispatlayabildiğini** olay olarak sunar. İspatlayamadığını
gruplamaz, yalnızca işaret eder. Bu, topoloji modeline sahip olmanın somut
getirisidir; önceki üründe bu ayrım yapılamazdı çünkü graf yoktu.

### 6. Wallboard ayrı bir görünümdür

Wallboard, Triyaj ekranının tam ekran modu **değildir**. Farklı bir üründür:

| | Triyaj | Wallboard |
|---|---|---|
| Okuma mesafesi | ~60 cm | 3+ metre |
| Etkileşim | Tıklama, filtreleme, aksiyon | Yok |
| Tipografi | Yoğun | Uzaktan okunur |
| Çalışma süresi | Oturum | Haftalarca kesintisiz |
| Hover / odak | Anlamlı | Anlamsız |

Ayrı rota, kendi düzeni — **ama aynı model**, aynı alarm durumu. Kod paylaşımı
veri katmanındadır, sunum katmanında değil.

Wallboard'a özgü teknik gereksinimler:
- Haftalarca açık kalacağı için bellek sızıntısı kabul edilemez.
- **Bağlantı koptuğunda bayat veri taze gösterilmez.** Ekranda "son güncelleme
  14 dakika önce" belirir; sessizce eski değer gösterilmez (ilke 1).

## Değerlendirilen alternatifler

### Alternatif A: Satıcıya göre gruplamayı korumak

Operatörün mevcut kas hafızasına en yakın, geçiş maliyeti sıfır.

Seçilmedi çünkü operatörün görevine değil sistemin iç yapısına göre
düzenlenmiş bir arayüz, her yeni satıcı entegrasyonunda bir sayfa daha ekler
ve 60 sayfalık dağınıklık kaçınılmaz olur. Kas hafızası, kademe 3 alt menüsü
ile zaten korunuyor.

### Alternatif B: Yalnızca tek jenerik varlık gezgini

En temiz, en az bakım.

Seçilmedi çünkü satıcıya özgü derinlik genelleşmez. iLO'nun termal sensör
haritası veya MPIO yol matrisi jenerik bir tabloya sığmaz; sığdırmaya
çalışmak ürünün farklılaştırıcı yeteneğini yok eder.

### Alternatif C: Tüm alarmları yalnızca düz liste olarak göstermek

En dürüst, gruplama hatası riski yok.

Seçilmedi çünkü alarm yorgunluğu ürünün çözmeyi vaat ettiği problem. Bir SFP
arızasının ürettiği 12 satırı 12 ayrı sorun gibi göstermek, tam olarak
kaçınılmak istenen durum. Korelasyonun iki sınıfa ayrılması (§5.2) riski
yönetiyor.

## Sonuçlar

### Olumlu

- Çift durum yapısal olarak imkânsız: tek alarm deposu, çok projeksiyon.
- Yeni satıcı eklemek üst seviye navigasyonu değiştirmez.
- Fiziksel triangülasyon ilk kez gezinilebilir.
- Alarm yorgunluğuna karşı korelasyon, güven sınıfına göre uygulanıyor.
- Operatörün mevcut alışkanlığı (iLO ekranı) korunuyor.

### Olumsuz / kabul ettiğimiz bedel

- **Üç kademe, öğrenme eğrisi demek.** Yeni operatör "nereye bakacağım"
  sorusunu ilk günlerde daha çok sorar. Arama ve varlık gezgini bunu
  hafifletmeli.
- Kademe 3 ekranları hâlâ ayrı bakım kalemi; sayıları kontrolsüz artarsa
  eski dağınıklık farklı bir isimle geri gelir. Yeni derin görünüm eklemek
  gerekçe istemeli.
- Topolojik korelasyon, graf yanlışsa yanlış olay üretir. Kimlik çözümleme
  hataları burada görünür hale gelir — bu aslında iyi, ama kullanıcıya
  "neden bu gruplandı" açıklaması sunulmalı.
- Wallboard'ı ayrı görünüm yapmak kod paylaşımını azaltır; iki sunum katmanı
  bakımı gerekir.

### Bu kararı yeniden değerlendirmemiz gereken durum

- Kademe 3 derin görünüm sayısı 10'u aşarsa gruplama yeniden ele alınmalı.
- Sahada operatörler olay gruplamasını kapatıp sürekli düz listede çalışıyorsa,
  korelasyonun güven eşiği veya sunumu yanlış demektir.
