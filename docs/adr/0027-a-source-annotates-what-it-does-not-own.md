# ADR-0027: Sahibi olmadığı varlığa kaynak açıklama ekler, yerine geçmez

- **Durum:** Önerildi
- **Tarih:** 2026-09-23
- **Karar verenler:** Ertuğrul Ünal
- **İlgili:** ADR-0003 (topoloji önce varlık modeli), ADR-0004 (ilişki ve yaşam döngüsü),
  ADR-0005 (toplayıcı sözleşmesi), ADR-0025 (toplayıcı yalnız okur), ADR-0026 (üç değerli
  değerlendirme, ileri taşıma sınırı), `product-architecture.md` ilke 3 (kimlik katlama)

## Bağlam

İkinci satıcı (M6) ilk kez aynı fiziksel şeyi iki kaynaktan gösteriyor:
SimpliVity'nin host'u vSphere'in ESXi host'udur (`hypervisor_object_id` =
`<vCenter instanceUuid>:HostSystem:host-N`, Kibar'da 26/26 ölçüldü), OmniStack
kümesi vSphere kümesidir, OVC bir VM'dir. İlke 3 bunların **tek varlık**
olmasını ister; ekranda "host" iki kez görünmez.

Bugünkü birleştirme buna hazır değil. `EntityGraph.Merge`
(`Domain/EntityGraph.cs:116–123`) kimliğe göre **bütünüyle yerine koyar**;
`InventoryCollectionPipeline.cs:118–123` varlığı getiren anlık görüntünün
`SourceInstanceId`'sini damgalar; kaybolma (`:98`), ilişkiler (`:146–151`) ve
alarm sahipliği (`MonitoringCycle.cs:327`) o damgayı izler. İki kaynak aynı
kimliği verirse son gelen kazanır: ya vSphere'in ayarları, boyutlandırması,
depolama yolları ve DRS kuralları silinir, ya da SimpliVity'nin verisi
sessizce kaybolur. Bu, #127'deki "aynı satıra iki kez dokunma" hatasının
varlık düzeyindeki hâlidir. Yedek tazeliği kontrolü de aynı anahtarlara iki
yazar istemez (`BackupFreshnessCheck.cs:60–93`, Commvault her tur yeniden yazar).

Referans ürünler bu sorunu sahiplikle çözüyor: vROps/Aria'da bir nesnenin
**tek bir adaptörü** vardır, diğer adaptörler ona özellik ve metrik **ekler**;
Dynatrace'te ikinci kaynaktan gelen veri var olan varlığa birleştirilir, yeni
varlık açmaz.

## Karar

**Bir varlığın tek sahibi vardır: onu envantere getiren kaynak. Başka bir
kaynak o varlığı yaratamaz, adını, türünü, işaretlerini ve ilişkilerini
değiştiremez; yalnızca kendi ad alanında açıklama ekler.**

1. Anlık görüntü bir `Annotations` listesi taşır: `(EntityId, ad alanlı
   ayarlar)`. Birleştirme bunları **yalnızca var olan** varlığa uygular;
   olmayan kimlik "katlanamadı" olarak toplayıcının kısmi hatasıdır, tahmin
   yoktur.
2. Anahtarlar kaynağın ad alanındadır (`simplivity.*`). Aynı ad alanına iki
   kaynağın yazması hatadır; birleştirme fırlatır.
3. Kaynak sessizken ad alanı ileri taşınır; sınır ADR-0026'nın ileri taşıma
   sınırıyla aynıdır (ham saklama, 2 gün). Sonrası "bilinmiyor"dur, eski
   değer değil.
4. Kimlik çözümü toplayıcıda değil, Host'ta salt-okunur bir arama portundadır
   (`GraphSampleTargetProvider` deseni): toplayıcı grafı okumaz (ADR-0025).
5. Aynı olguyu iki kaynak söylüyorsa (son yedek zamanı) kural **yenisini**
   alır ve hangi kaynağın olduğunu gözlemde yazar.

Kapsam dışı: metrikler (seriler zaten varlık kimliğiyle kaynak-bağımsız);
alarmlar (bir anlık görüntü varlığı getirmeden onun üzerinde alarm
üretebilir — mevcut davranış, `AlertReconciler.cs:599`).

## Gerekçe

- İlke 3 (tek varlık) ile ADR-0004 (sahiplik ve kaybolma) çelişmeden birlikte
  durur: sahip bir tanedir, katkı çoktur.
- Ad alanı çakışmayı yapıyla imkânsız kılar; "dikkatli yaz" kuralı değil.
- Sessizlikte eski değeri sonsuza kadar taşımak ADR-0026'nın reddettiği
  şeydir; aynı sınır burada da geçerli.

## Değerlendirilen alternatifler

### Alternatif B: yalnız alarm, varlık yok
SimpliVity yalnız vSphere kimlikleri üzerinde alarm üretir. Alan değişikliği
yok; ama yükseltme durumu, arbiter durumu ve yedek tazeliği ekranda yer
bulamaz. M6-S'nin yarısı düşer. Seçilmedi.

### Alternatif C: ayrı varlıklar, işaretlerle bağlı
"OmniStack host" ayrı varlık, kimlik işaretiyle ESXi host'a bağlanır. İlke
3'ü ihlal eder: aynı sunucu iki satır olur, sağlık iki yerde türetilir.
Seçilmedi.

### Alternatif D: son gelen kazanır (bugünkü davranış)
Veri kaybı sessiz. Seçilmedi; #127 aynı sınıfın hatasıydı.

## Sonuçlar

### Olumlu
- İkinci, üçüncü kaynak (Redfish, dizi, SAN) aynı yoldan eklenir; her biri
  kendi ad alanıyla.
- vSphere'in verisi ikinci kaynaktan etkilenmez; bu yayım öncesi/sonrası
  Settings/Marks/DrsRules sayımıyla kanıtlanır.

### Olumsuz / kabul ettiğimiz bedel
- Domain (`EntityGraph.Merge`), Application (boru hattı) ve bir kural
  değişir; Settings JSON olarak saklanmıyorsa migrasyon gerekir.
- Kaynak sessizken 2 güne kadar eski açıklama görünür; ekran "ileri
  taşındı" demeli (ADR-0026'daki bayat işaretiyle aynı).

### Bu kararı yeniden değerlendirmemiz gereken durum
- Bir kaynağın sahibi olmadığı varlıkta **ilişki** eklemesi gerekirse
  (ör. dizi → datastore eşlemesi, M7): o zaman açıklama yetmez, ilişki
  sahipliği ayrı karardır.
- İki kaynak aynı olguyu sürekli çelişkili söylerse "yenisini al" kuralı
  yetersiz kalmış demektir.
