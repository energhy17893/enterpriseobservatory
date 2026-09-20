# Öneri: yokluk iyileşme değildir

**Durum: öneri.** Doğrulanmış bir kusur, altı referans üründen alınmış bir
çözüm, ve motoru gerektirmeyen bir uygulama.

---

## 1. Kusur

[`AlertLifecycle.OnAbsent`](../../src/EnterpriseObservatory.Domain/Alerts/AlertLifecycle.cs)
doğrulanmış bir alarmı **ilk yokluk turunda** kapatıyor:

```csharp
var resolved = existing
    .With(AlertLifecycleState.Resolved, AlertTransitionReason.ConditionCleared, nowUtc)
```

İki ayrı sorun var ve ikisi de aynı kökten geliyor — **uzlaştırma, kendisine
verilenin tüm gerçek olduğunu varsayıyor** (`AlertReconciler`'ın kendi yorumu).

### 1a. Histerezis asimetrik, ama yanlış yönde

Açılışta `ConsecutiveHits` ile doğrulama bekleniyor. Kapanışta hiçbir şey
beklenmiyor. Referans ürünlerin tamamı bunun tersini yapıyor: **açılışta yavaş,
kapanışta da yavaş.**

### 1b. Denetim izi körlüğü iyileşmeden ayıramıyor

`AlertTransitionReason` listesinde yokluk için tek çıkış `ConditionCleared` —
yani "arıza geçti". Bir kural patladığında, [`GuardedRule`](../../src/EnterpriseObservatory.Application/Analysis/GuardedRule.cs)
hatayı alarm olarak bildiriyor (doğru), ama o kuralın açık alarmları
**"arıza geçti" damgasıyla** kapanıyor. Altı ay sonra o kaydı okuyan kimse
farkı anlayamaz.

`GuardedRule` bunu zaten dürüstçe yazıyor. Bu öneri, yazmakla yetinmeyip
düzeltiyor.

---

## 2. Referans ürünler: altıda altı, yokluğu sağlık saymıyor

| Ürün | Mekanizma |
|---|---|
| **Grafana** | `Error` ve `No Data` birinci sınıf kural durumları. Kaybolan seri için **iki değerlendirme aralığı** son durumu korur, sonra `grafana_state_reason: MissingSeries` **etiketiyle** kapanır |
| **Prometheus** | `ValidUntil = 4 × max(interval, resendDelay)`, kaynak yorumu: *"iki değerlendirme veya gönderim hatasına izin ver"*. Hatalı kural için staleness işareti **yazılmaz** — `active` haritası dokunulmadan kalır |
| **Zabbix** | Trigger **state** (Normal/Unknown), **value**'dan (OK/PROBLEM) bağımsız. `Unknown` ifade boyunca yayılır: `1 and Unknown → Unknown`. Açık problemi OK'a çeviremez |
| **Netdata** | `UNDEFINED` (0), `CLEAR` (1)'den ayrı bir durum. Kuralın **silinmesi** bile kendi durumunu alır (`REMOVED`) |
| **Dynatrace** | Kural başına *"eksik veri ihlal sayılsın mı"* seçimi; kendi belgeleri seyrek serilerde alarm fırtınasına karşı uyarıyor |
| **vROps** | İptal yalnızca koşul **false** olduğunda (Cancel Cycle). Yokluk hiçbir iptal yolunda geçmiyor; kendi ayrı alarmı var |

Kaynaklar [reference-approaches.md](../reference-approaches.md)'ye işlendi.

**Grafana'nın modeli bize en yakın olanı**, çünkü kapatmayı yasaklamıyor —
**etiketliyor**. Kapanış yine olur, ama kaydı neden kapandığını söyler.

---

## 3. Önerilen değişiklik

Üç parça, hiçbiri motor gerektirmiyor.

### P1 — Alarmın hangi kuraldan geldiğini bil

`AlertDefinition` ve `AlertInstance` üzerine bir `RuleId`. Bu, diğer iki
parçanın da etkinleştiricisi.

**Bunun ne olmadığı önemli:** bir `IAnalysisRule` arayüzü değil, kimliğe göre
bir sözlük. Prometheus'un kural başına sağlığı, Zabbix'in trigger state'i ve
Netdata'nın alarm durumu — üçü de tam olarak bu: bir **kayıt defteri**, çok
biçimli bir arayüz değil.

Bu, [`rule-engine.md`](rule-engine.md) §7'nin *"kural başına sağlık kaydı —
yani motorun bir parçası daha"* cümlesini **düzeltiyor**. Ayrılabilir, ve
önce yapılmalı. Motorun tetikleri (üçüncü motor-kapsamı kural, ya da geçmişe
bakan ilk kural) bu değişiklikten etkilenmiyor ve yerinde duruyor.

### P2 — Çalışmayan kuralın alarmları kapanmaz, bekler

`AlertReconciliationRequest`'e o turda **patlayan kuralların kimlik kümesi**
eklenir. `OnAbsent`, alarmın sahibi kural o turda çalışmadıysa **tutar**,
kapatmaz.

Bu güvenli, çünkü `GuardedRule` aynı anda hata alarmını açıyor: operatör
körlüğü, bulguların kaybolduğu anda görüyor.

### P3 — Kapatmak zorunda kalınca, sebebi damgala

Yeni bir `AlertTransitionReason` üyesi — `NotEvaluated`. P2'nin kapsamadığı
durumlar için: kural kaldırıldı, kaynak susuyor, tur hiç koşmadı.

Grafana'nın `MissingSeries` damgasının karşılığı. Kapanış yine olur; kayıt
yalan söylemez.

---

## 4. Reddedilen alternatif: genel bekleme süresi

Kapanışa toptan iki turluk bir gecikme koymak — Prometheus'un ve Grafana'nın
yaptığı. **Reddediyorum**, ve gerekçe referansların kendi gerekçesinde:

Prometheus'un toleransı, değerlendirici ile bildirici **ayrı süreçler** olduğu
ve aralarında yalnızca en-az-bir-kez teslim sözleşmesi bulunduğu için var.
Bilgi eksikliğini zamanla telafi ediyorlar. Bizde tek süreç ve tek depo var —
**alarmın neden yok olduğunu biliyoruz.**

Bildiğimiz bir şeyi zamanla tahmin etmek, hem her gerçek iyileşmeyi 30 saniye
geciktirir hem de denetim izini P3'ün sağladığı bilgiden yoksun bırakır.
Hedefli düzeltme kesinlikle daha iyi.

Genel beklemenin sırası, *"kural koştu ve gerçekten söyleyemiyorum"* diyen bir
kural çıktığında gelir. Bu, *"kural patladı"*dan farklı bir şeydir ve bugün
öyle bir kural yok.

---

## 5. Benimsenmeyecekler

| Ne | Neden |
|---|---|
| Alarm örneğinde üçlü `Unknown` durumu (Zabbix, Netdata) | Kavramsal olarak en doğrusu, ama `AlertLifecycle`'a durum ekler — kodun kendi yorumu orayı *"ürünün davranışsal olarak en ince yeri"* diyor. Her okuyucu (gelen kutusu, varlık sayfası, bildirici) öğrenmek zorunda kalır. P1-P3 operatöre aynı faydayı durum kümesine dokunmadan verir |
| Dynatrace'in *"eksik veri ihlaldir"* varsayılanı | Kendi belgeleri seyrek serilerde alarm fırtınası uyarısı yapıyor, ve bizim estate'imiz meşru seyrek serilerle dolu (SIOC kapalı olduğu için `datastoreVMObservedLatency` her yerde sıfır okuyor). Benimsenecekse kural başına ve opt-in |
| Prometheus'un `ValidUntil` zaman aşımı | §4'teki gerekçe |
| Symptom/alert bileşim katmanı | [rule-engine.md](rule-engine.md) §5'teki erteleme bu araştırmadan etkilenmedi |

---

## 6. Sonra bakılacak: kural başına son başarılı değerlendirme zamanı

Prometheus `rule_group_last_evaluation_timestamp_seconds` ve kural başına
`lastEvaluation` yayınlıyor; Grafana kural sağlığını listede gösteriyor.

Bugünkü `GuardedRule` alarmı bir kuralın **bu turda** patladığını söyleyebiliyor
ama *"ne zamandır buradan körüz"* sorusunu yanıtlayamıyor — operatörün gerçekte
sorduğu soru bu. P1 geldikten sonra bu yalnızca bir zaman damgası.
