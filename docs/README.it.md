# OpenWidget

**[Scarica la versione portatile per Windows x64](https://github.com/Kibra2025/OpenWidget/releases/latest)** · [English overview](../README.md)

Scarica lo ZIP **Portable** nella sezione Assets della release: gli archivi Source code non contengono il programma pronto da avviare. Interfaccia in italiano; Windows 10/11 x64 e .NET Framework 4.8 richiesti.

Widget desktop per Windows 10/11 x64, scritto in C# e WPF, per CPU, tutte le GPU rilevate, RAM e rete.

## Versione portatile

1. Estrai `OpenWidget-Portable-1.7.0-win-x64.zip`.
2. Avvia `OpenWidget.exe` o `Avvia.bat` dalla cartella estratta e accetta la richiesta di amministratore di Windows.
3. Mantieni insieme l'eseguibile, le DLL e le sottocartelle `tools` e `licenses`.

L'applicazione non richiede un installer né un nuovo runtime .NET moderno. Usa .NET Framework 4.8: se non è già presente nel sistema Windows, questo componente Microsoft deve essere installato. La prima esecuzione non abilita l'avvio automatico; le impostazioni precedenti vengono mantenute.

`config.json`, il suo backup e `widget.log` sono salvati accanto all'eseguibile. Se la cartella è di sola lettura, vengono salvati in `%LOCALAPPDATA%\DeskHardwareWidget`. Le impostazioni già presenti in questa cartella vengono mantenute anche dopo il passaggio all'esecuzione elevata, se non esiste una configurazione portatile. Lo ZIP non contiene impostazioni personali né log del PC su cui è stato compilato.

## Amministratore e avvio automatico

Il manifest dell'eseguibile richiede sempre **privilegi di amministratore**, anche per diagnostica e riavvio. Gli avvii manuali mostrano la richiesta UAC quando il processo chiamante non è già elevato; annullandola il widget non parte.

Per l'avvio automatico seleziona **Avvia con Windows come amministratore** nel menu. Il widget registra nell'Utilità di pianificazione l'attività `DeskHardwareWidget-<SID utente>`, con trigger all'accesso dello stesso utente e **Esegui con i privilegi più elevati**. Dopo la configurazione, l'avvio all'accesso non richiede una nuova conferma UAC. L'attività usa la sessione desktop dell'utente, non salva password, parte anche a batteria e resta attiva senza limite di durata. L'utente deve appartenere al gruppo Amministratori.

L'eventuale voce precedente nella chiave Run viene migrata automaticamente al primo avvio della nuova versione e rimossa dopo la registrazione riuscita. Se sposti la cartella, avvia una volta il widget dalla nuova posizione per aggiornare il percorso. Deselezionare la voce nel menu elimina l'attività e l'eventuale vecchia voce Run. Gli errori vengono segnalati nel widget e registrati in `widget.log`.

Puoi configurare l'avvio anche da terminale, accettando l'eventuale UAC (codice di uscita 0 se riuscito, 1 in caso di errore):

```powershell
Start-Process .\OpenWidget.exe -ArgumentList '--enable-autostart' -Verb RunAs -Wait
Start-Process .\OpenWidget.exe -ArgumentList '--disable-autostart' -Verb RunAs -Wait
```

Riferimenti Microsoft: [contesti di sicurezza delle attività](https://learn.microsoft.com/en-us/windows/win32/taskschd/security-contexts-for-running-tasks), [avvio all'accesso dell'utente](https://learn.microsoft.com/en-us/windows/win32/taskschd/starting-an-executable-when-a-user-logs-on).

## Temperature CPU

La temperatura proviene dai sensori CPU di LibreHardwareMonitor 0.9.6. La vecchia zona termica ACPI/WMI, che poteva restare fissa e non rappresentava necessariamente il processore, è stata rimossa.

La lettura dei registri del processore richiede **PawnIO 2.2 o successivo**, un driver di sistema separato, e privilegi di amministratore. Il programma resta portatile; il driver deve essere installato una volta su ciascun PC in cui si vogliono usare queste temperature.

- Dal menu seleziona **Abilita temperature CPU (installa PawnIO)**.
- Accetta la richiesta UAC e completa l'installazione dell'installer ufficiale firmato incluso in `tools`.
- Al termine il widget si riavvia come amministratore. Se annulli, CPU, GPU, RAM e rete continuano a essere disponibili nei limiti dei sensori esposti.
- Anche gli avvii successivi, manuali e automatici, vengono eseguiti come amministratore.

Il programma non installa driver automaticamente e non mostra temperature fittizie al posto di un sensore assente. Per un sensore CPU non supportato, mostra un messaggio esplicito anche dopo l'installazione del driver.

## GPU

Ogni GPU rilevata ha una scheda e una cronologia indipendenti. La GPU dedicata viene mostrata prima di quella integrata. Sono supportate le GPU NVIDIA, AMD e Intel esposte dalla libreria.

- Carico e temperatura, quando disponibili.
- VRAM usata/totale per la dedicata; memoria condivisa per l'integrata.
- Temperature con un decimale e indicazione esplicita dei dati non disponibili.
- Alcune GPU integrate espongono carico e memoria condivisa ma nessun sensore di temperatura. Il widget indica il dato come non disponibile.

Le letture NVIDIA sono gestite dalla libreria, tramite le API del driver e i relativi fallback, evitando di bloccare il recupero dei dati quando una singola API fallisce.

## Controlli

Un clic sull'icona nell'area notifiche apre il **pannello di gestione**, con lo stesso stile scuro del widget. Anche il doppio clic apre il pannello; se è già aperto o ridotto a icona, lo riporta in primo piano senza creare altre finestre.

Il pannello raccoglie tutte le impostazioni in cinque sezioni:

- **Temperature**: CPU, interruttore indipendente per ogni GPU rilevata, comandi **Mostra tutte** e **Nascondi tutte**, visibilità indipendente di RAM e rete, modalità compatta. Puoi mostrare entrambe le GPU, soltanto la dedicata, soltanto l'integrata o nessuna GPU. La selezione viene salvata per identificativo della scheda e mantenuta al riavvio o dopo un cambio d'ordine; una nuova GPU è visibile per impostazione predefinita. Nascondere una scheda riguarda la sua visualizzazione nel widget. La finestra usa un unico sfondo, senza card dei singoli elementi, mantenendo gli accenti di CPU, GPU, RAM e rete.
- **Batterie**: seconda finestra indipendente con anelli di carica per il PC e gli accessori, filtri, blocco, click-through, centratura e frequenza di lettura.
- **Posizione**: due gruppi separati, Temperature e Batterie, ciascuno con coordinate X/Y, centratura sul monitor principale, blocco e click-through. La centratura compila le coordinate: **Applica** le salva e sposta la finestra interessata. Puoi impostare la posizione delle batterie anche quando il widget è nascosto. Attivare il click-through blocca la relativa posizione; sbloccarla lo disattiva.
- **Impostazioni**: avvio automatico come amministratore e intervallo di aggiornamento da 250 a 60.000 ms, applicato senza riavvio.
- **Sensori e diagnostica**: stato della temperatura CPU, PawnIO, nuova rilevazione, riavvio elevato e log diagnostico.

Premi **Applica** per salvare le modifiche o **Annulla modifiche** per rileggere la configurazione attiva. Chiudere il pannello lascia il widget in esecuzione e scarta le modifiche non applicate. **Esci dal widget** applica le modifiche valide e termina l'app. I comandi che riavviano il widget applicano prima le modifiche in sospeso.

Puoi aprire il pannello anche dalla voce **Gestisci widget e impostazioni…** del menu rapido. Il tasto destro sul widget, i puntini di una scheda e il menu dell'icona nell'area notifiche continuano a offrire le stesse azioni rapide:

- Blocco della posizione e trascinamento quando sbloccato.
- Click-through; lo sblocco lo disattiva per consentire il trascinamento.
- Avvio automatico con Windows come amministratore, con migrazione della vecchia voce Run e aggiornamento del percorso dell'eseguibile.
- Visibilità indipendente di ogni GPU anche dalle voci **Mostra GPU…** del menu rapido.
- Modalità compatta: mantiene CPU e le GPU selezionate, nascondendo RAM e rete.
- Centratura sul monitor principale e ripristino su un'area visibile dopo cambi di monitor.
- Installazione/configurazione PawnIO, riavvio elevato, nuova rilevazione dei sensori e log diagnostico.
- Uscita, con chiusura del campionamento e rilascio delle risorse.

La finestra viene mantenuta in fondo all'ordine delle finestre. Non viene impostata come sempre in primo piano.

L'eseguibile, le finestre e l'area notifiche condividono il logo con l'onda ciano su fondo blu scuro. L'icona è incorporata nell'app in sette dimensioni, da 16 a 256 pixel; le sorgenti grafiche si trovano in `Assets`.

## Widget batterie

La seconda finestra usa lo stesso sfondo scuro e la stessa trasparenza del widget Temperature, con un unico contenitore arrotondato, senza titolo, icona d'intestazione, card individuali o scritte di intestazione/piè di pagina. Sono sempre presenti quattro spazi circolari: quelli senza dispositivo restano anelli grigi vuoti. Oltre quattro dispositivi vengono aggiunte righe da quattro. La finestra è compatta (circa 506 × 230 unità logiche, inclusi i margini dell'ombra). Le icone sono al centro degli anelli; sotto compaiono il nome, la percentuale e una breve indicazione dello stato di ricarica. I dettagli completi restano disponibili al passaggio del mouse. La batteria PC usa il ciano, gli accessori il verde e la ricarica attiva il verde; i livelli bassi diventano ambra (20% o meno) e rosa (10% o meno).

Il fulmine verde compare al centro della parte alta del cerchio per una ricarica confermata. Quando il PC è collegato all'alimentatore senza ricarica attiva, compare invece una spina ciano e l'indicazione **Collegato · non in carica**. Per gli accessori che non comunicano lo stato di ricarica compare **Ricarica N/D**: una percentuale nota non prova che il dispositivo stia caricando.

La posizione è indipendente e si salva trascinando la finestra quando sbloccata. Per gestirla apri **Batterie** nel pannello, **Gestisci batterie…** nell'area notifiche o il menu con il tasto destro sulla nuova finestra. Puoi mostrarla/nasconderla, filtrare PC e accessori, includere dati sconosciuti o dispositivi scollegati, bloccarla, attivare il click-through, centrarla e richiedere una nuova lettura. Chiuderla nasconde soltanto il widget batterie; l'uscita dall'app chiude entrambe le finestre e ferma entrambi i campionamenti.

**Click-through batterie** è disponibile direttamente nel menu del widget e **Click-through widget batterie** nel menu dell'area notifiche, utilizzabile anche per disattivarlo quando il widget non riceve più clic. Attivarlo blocca la sola finestra batterie; il widget hardware conserva le proprie impostazioni.

Il PC viene letto ogni 5 secondi tramite `GetSystemPowerStatus`, senza richiedere una batteria su un PC fisso. Gli accessori sono rilevati ogni 30 secondi (configurabile da 10 a 300) attraverso le proprietà dei dispositivi esposte da Windows. Non vengono avviati accoppiamenti o installati driver. I nodi Bluetooth di uno stesso dispositivo vengono raggruppati per contenitore per evitare duplicati; sono inclusi anche altri dispositivi che espongono una percentuale valida tramite queste proprietà.

**MX Master / Logitech HID++**: il widget interroga direttamente le interfacce Logitech, inclusi i ricevitori Logi Bolt e Unifying e le interfacce Bluetooth compatibili. Legge il nome, la percentuale e lo stato di ricarica tramite le feature `0x1004` (batteria unificata) o `0x1000` (legacy). Mostra separatamente **In carica**, **Ricarica lenta**, **Carica completa**, **A batteria** ed eventuali errori. Il fulmine appare solo durante la ricarica attiva. Le percentuali non supportate restano sconosciute; le letture legacy possono avere la granularità decisa dal firmware. Il livello zero legacy significa sconosciuto, mentre lo zero della batteria unificata è valido solo se il dispositivo dichiara il supporto alla percentuale.

La comunicazione HID++ è integrata nell'eseguibile. Il trasporto deriva dal codice MIT di LogiCopy+, incluso nel progetto con licenza in `licenses/LogiCopy-MIT.txt`. Non richiede Logi Options+, Node.js o programmi di supporto. Esegue solo richieste di lettura, senza modificare DPI, pulsanti, profili o associazioni. Ogni scansione chiude le interfacce HID; i dispositivi già rilevati che non rispondono restano visibili con lo stato Lettura non disponibile, senza riutilizzare vecchie percentuali o il fulmine di ricarica. Questo vale anche quando i filtri nascondono dispositivi scollegati o percentuali sconosciute. Dopo 3 scansioni consecutive senza risposta al ping, il dispositivo passa a Scollegato e segue il filtro dei dispositivi scollegati (con l'intervallo predefinito, entro circa 90 secondi dallo spegnimento o dal cambio PC). Una risposta al ping azzera il conteggio; un errore della sola lettura batteria non viene interpretato come disconnessione. Una lettura successiva riuscita ripristina automaticamente i dati. Le letture HID++ e Windows dello stesso contenitore vengono unite; mouse distinti con lo stesso nome restano separati.

La compatibilità dipende dai dati resi disponibili da Windows e dal dispositivo: non tutti i telefoni, orologi, auricolari o accessori USB espongono la carica. Non vengono inventate percentuali o stati di ricarica. Un livello sconosciuto è rappresentato da un anello tratteggiato e un trattino; un dispositivo scollegato non conserva una vecchia percentuale come se fosse attuale. Windows può aggiornare i dati degli accessori a intervalli propri; quando disponibile, il timestamp di Windows è nel suggerimento al passaggio del mouse. L'autonomia stimata del PC compare nel suggerimento soltanto se fornita dal sistema.

Riferimenti: [stato energetico del PC](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-system_power_status), [profili batteria Bluetooth](https://learn.microsoft.com/en-us/windows-hardware/design/accessory-guidelines/bluetooth-accessory-guidelines/bluetooth-accessory-guidelines-battery-profiles-services), [percentuale batteria dei dispositivi](https://learn.microsoft.com/en-us/windows/win32/properties/props-system-devices-batterylife), [stato di ricarica Windows](https://learn.microsoft.com/en-us/windows/win32/properties/props-system-devices-chargingstate), [specifica Logitech HID++ 2.0, feature legacy](https://lekensteyn.nl/files/logitech/logitech_hidpp_2.0_specification_draft_2012-06-04.pdf), [mappatura della batteria unificata nel driver Linux](https://github.com/torvalds/linux/blob/master/drivers/hid/hid-logitech-hidpp.c).

**DualSense / DualSense Edge**: il widget legge direttamente i report HID dei controller Sony collegati via **Bluetooth o cavo USB**, usando il protocollo della repo [DualSenseClient](https://github.com/DualSenseClient/DualSenseClient/tree/5e9c019b138e15ca85949994d9258f895940b84e). Mostra percentuale, **A batteria**, **In carica**, **Carica completa**, alimentazione USB senza ricarica ed eventuali errori di tensione, temperatura o ricarica. Il fulmine compare soltanto durante la ricarica attiva; a carica completa scompare. Il tipo di connessione e l'ora della lettura sono nel suggerimento.

La percentuale è indicativa: il controller comunica livelli a intervalli del 10%, convertiti come in DualSenseClient. Per esempio, il livello 9 a batteria corrisponde a 95%; il livello 8 durante la ricarica a 70%. I dati mancanti o non validi restano sconosciuti. I report Bluetooth semplici non contengono la batteria e vengono ignorati; una richiesta di lettura della calibrazione abilita i report completi, verificati anche con CRC32.

I controller **USB** vengono inclusi anche quando Windows non espone una percentuale o l'interfaccia HID è occupata: rimangono visibili con **USB · batteria N/D**, anche con il filtro delle percentuali sconosciute disattivato. Non viene dedotta la ricarica dalla sola presenza del cavo. Più controller hanno anelli separati; le interfacce audio e HID dello stesso controller vengono raggruppate. Quando Windows segnala un collegamento o una rimozione, il widget richiede una nuova scansione al successivo ciclo di lettura (normalmente entro 5 secondi), senza attendere l'intervallo ordinario degli accessori.

Non serve installare o avviare DualSenseClient: l'integrazione usa HidSharp, già incluso nel widget, ed è compatibile con .NET Framework 4.8. Non invia comandi per luci, vibrazione, grilletti, profili o associazioni. L'identità del controller e i contenitori Windows evitano doppioni fra le sorgenti e al cambio di trasporto; due controller con lo stesso nome restano distinti. Alla disconnessione i dati precedenti vengono rimossi. La scansione usa lo stesso intervallo degli altri accessori. Se un'altra app o un driver impedisce l'accesso HID, viene mantenuta un'eventuale lettura Windows valida; altrimenti la carica resta non disponibile. I riferimenti del protocollo sono in `licenses/DualSense-protocol-reference.txt`.

## Correzioni incluse

- Campionamento in un thread dedicato e aggiornamento dell'interfaccia tramite dispatcher.
- Valori predefiniti applicati anche ai JSON incompleti; intervallo validato fra 250 ms e 60 s.
- Salvataggio atomico e recupero del backup in caso di JSON corrotto.
- Grafici adattati all'area reale, con tutti i campioni visibili e lacune per dati mancanti.
- Scala automatica della rete, unità decimali Kbps/Mbps e contatori separati per ogni scheda.
- Gestione di coordinate negative, dimensioni reali del widget e area di lavoro dei monitor.
- Dipendenze transitive complete e binding redirect per .NET Framework.
- Log diagnostico limitato nelle dimensioni e nelle ripetizioni.

## Build, verifiche e pubblicazione

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -RunTests
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\publish.ps1
```

Oppure usa `build.bat` e `publish.bat`. La build usa MSBuild incluso in .NET Framework. Al primo avvio scarica le dipendenze dalle release ufficiali e il targeting pack Microsoft 4.8 nella cache locale `.build`, se necessario, verificandone i checksum. Non servono Visual Studio o l'SDK .NET moderno.

- Eseguibile compilato: `bin\Release\OpenWidget.exe`.
- Cartella portatile: `dist\OpenWidget-Portable`.
- ZIP portatile: `dist\OpenWidget-Portable-1.7.0-win-x64.zip`.
- Test: `tests\RegressionTests.cs`; includono manifest amministratore incorporato nell'EXE, definizione dell'attività pianificata, percorsi con spazi e caratteri XML, aggiornamento del percorso, configurazione, sensori mancanti, due GPU, grafici, monitor, pannello di gestione, validazione dei valori, persistenza, istanza unica e finestre reali con chiusura e click-through. Generano anche anteprime PNG del widget e delle sezioni del pannello.

Per registrare cinque campioni hardware senza aprire la finestra o modificare l'avvio automatico:

```powershell
.\OpenWidget.exe --diagnostics .\diagnostics.json 5
```

La diagnostica richiede gli stessi privilegi delle letture normali. Versioni, provenienza e SHA-256 delle dipendenze sono in `dependencies.json`; gli avvisi sono nella cartella `licenses`.
