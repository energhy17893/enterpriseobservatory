# ADR-0001: Deployment-agnostik çekirdekli modüler monolit

- **Durum:** Kabul edildi
- **Tarih:** 2026-09-18
- **Karar verenler:** Ertuğrul Ünal
- **İlgili:** ADR-0002

## Bağlam

Bu ürünün önceki sürümü (VMware Monitoring Platform, v4.3.58) üretimde çalışan,
505 testi geçen, gerçek müşteri değeri üreten bir kod tabanıydı. İki yapısal
sorunu vardı:

1. **Sınır yoktu.** Çekirdek kütüphane 142 dosya / 46.833 satırdı ve *tek bir
   klasörde, klasörsüz* duruyordu. 161 static sınıf, bunların ~45'i kalıcı durum
   tutan store'lardı. Enjeksiyon yok, izolasyon yok, birim testi zor.
2. **Süreçler arası iletişim diskti.** Worker servisi ile web uygulaması
   `%ProgramData%` altındaki bir JSON dosyası üzerinden haberleşiyordu.
   `MonitoringSnapshotStore.Current` bir *property* olmasına rağmen her erişimde
   dosya I/O yapıyor ve tüm eşzamanlı istekleri tek bir `lock` üzerinde
   sıralıyordu.

Bu iki sorun tek başına ölümcül değildi — ürün çalışıyordu. Ama birlikte, kod
tabanını **devredilemez** hale getirdiler: ikinci bir geliştirici katılamaz,
davranış izole test edilemez, dağıtım topolojisi değiştirilemezdi.

Aynı zamanda korumamız gereken bir kısıt var: ürünün en güçlü satış argümanı
**tek tıkla kurulan, ~120 MB RAM kullanan, ek appliance gerektirmeyen Windows
MSI paketi** olması. Ana rakip (VMware Aria Operations) 32+ GB RAM isteyen bir
vApp olarak geliyor. Kurulum basitliği teknik bir detay değil, ürünün kendisi.

Dolayısıyla karar noktası şu: ölçeklenebilirlik istiyoruz ama kurulum
basitliğini kaybedemeyiz.

## Karar

Çekirdeği dağıtım topolojisinden bağımsız yaz; topolojiyi bir *yapılandırma*
seçeneği yap.

Somut olarak:

- **Katmanlı yapı, tek yönlü bağımlılık.** `Domain` hiçbir projeye referans
  vermez. `Application` yalnızca `Domain`'e bakar. Adaptörler (collector,
  persistence, web) `Application`'ın tanımladığı port arayüzlerini uygular.
- **Çoklu composition root.** `Host.AllInOne` her şeyi tek proseste çalıştırır —
  MSI hedefi, bugünkü ürün vaadi. İleride `Host.Collector` + `Host.Web` ayrı
  proseslerde aynı kodu çalıştırabilir.
- **Süreçler arası kanal bir port arkasındadır.** `ISnapshotChannel`'ın
  in-process implementasyonu tek proseste bellek referansıdır; out-of-process
  implementasyonu dosya/Postgres/Redis olabilir. Topoloji değişikliği adaptör
  değişikliğidir, mimari değişikliği değil.
- **Domain'de static mutable state yasaktır** ve bu bir mimari testle zorlanır.

## Gerekçe

Önceki mimarinin hatası monolit olması **değildi**. Tek kutuya kurulan bir ürün
için monolit doğru seçimdir; mikroservis burada saf maliyettir.

Hata **sınırların olmamasıydı.** 46 bin satır tek namespace'te birikti çünkü onu
engelleyen hiçbir mekanizma yoktu. Aynı disiplinsizlik mikroservis mimarisinde
dağıtık bir çamur yumağı üretirdi — daha da kötüsü.

Dolayısıyla çözüm "mimariyi değiştir" değil, **"sınırları makineye zorlat"**.
İnsan disiplini bir yıl sonra gevşer; CI'da kırmızıya düşen bir test gevşemez.

Deployment-agnostik çekirdek ise gelecekteki seçenekleri açık tutar. Bugün tek
proses çalışıyoruz çünkü ürün vaadi bu. Yarın bir müşteri 5000 host'la gelirse
collector'ları ayrı proseslere bölmek bir yapılandırma işi olur, altı aylık bir
yeniden yazım değil.

## Değerlendirilen alternatifler

### Alternatif A: Mevcut mimariyi kademeli refactor et (strangler fig)

Çalışan 46 bin satırı yeni repoya taşıyıp parça parça temizlemek. En hızlı
başlangıç, IP korunur, 505 test gün 1'den yeşil.

Seçilmedi çünkü mevcut kodun asıl borcu *yapısaldı*, lokal değil: 45 static
store ve dosya tabanlı IPC, kod tabanının her yerine dokunuyor. Kademeli
temizlik, temizlenmemiş kısımlarla temizlenmiş kısımların aylarca bir arada
yaşaması demekti — pratikte iki mimariyi aynı anda taşımak.

Bu kararın bedeli açık ve kabul edilmiştir: **doğrulanmış saha mantığını yeniden
üretmek zaman alacak ve regresyon riski taşıyor.** Eski kod tabanı referans
olarak korunur ve davranış doğrulaması için okunur.

### Alternatif B: Mikroservis + Kubernetes

Her collector ayrı servis, mesaj kuyruğu, yatay ölçekleme.

Seçilmedi çünkü ürünün farklılaştırıcısını yok ederdi. Müşteri profili kurumsal
Windows veri merkezi; "bir MSI çalıştır, bitti" ile "bir K8s cluster'ı kur"
arasındaki fark, satışın kendisi. Ayrıca çözmediğimiz bir ölçek problemi için
peşin karmaşıklık ödemek olurdu — bugünkü en büyük kurulum tek bir vCenter
ortamı.

### Alternatif C: Hiçbir şey değiştirme, mevcut kodda devam et

Seçilmedi çünkü asıl sorun teknik değil, süreçseldi: kod devredilemez durumdaydı.
Bkz. ADR-0002.

## Sonuçlar

### Olumlu

- Sınırlar CI tarafından zorlanır; zamanla erimezler.
- `Domain` ve `Application` I/O'suz olduğu için testleri milisaniyeler sürer.
- Dağıtım topolojisi ertelenebilir bir karar haline gelir.
- Yeni satıcı collector'ı eklemek mevcut hiçbir dosyaya dokunmaz.
- Kod tabanı devredilebilir: yeni bir geliştirici katman haritasına bakarak
  nereye ne yazacağını bilir.

### Olumsuz / kabul ettiğimiz bedel

- **Başlangıç maliyeti yüksek.** Çalışan bir üründen sıfır satıra dönüyoruz.
  İlk aylarda görünür özellik üretimi yavaş olacak.
- **Katman geçişi ceremony gerektirir.** Domain'e bir alan eklemek, port
  arayüzüne ve adaptöre de dokunmayı gerektirebilir. Küçük değişiklikler için
  fazladan iş.
- **Saha bilgisi kaybı riski.** Eski koddaki 46 bin satır, yıllara yayılmış
  operasyonel bilgi içeriyor (örn. OneView alert'inin iLO IML'den önce
  sıralanması). Bunların yeniden keşfedilmesi gerekecek; eski kod bu yüzden
  referans olarak saklanmalı.
- In-process kanal implementasyonu ile out-of-process arasındaki davranış farkı
  (örn. serileştirme sınırları) test edilmezse gelecekte sürpriz üretir.

### Bu kararı yeniden değerlendirmemiz gereken durum

- Tek bir müşteri kurulumu tek prosesin kaynak sınırlarını zorlarsa
  (>2000 host veya >20 vCenter), `Host.Collector` ayrımı devreye alınmalı.
- SaaS / çok kiracılı barındırma bir iş hedefi haline gelirse, konteyner
  öncelikli dağıtım yeniden değerlendirilmeli.
