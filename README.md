# Swiss CLI

Un "coltellino svizzero" CLI ad alte prestazione scritto in C# (Native AOT) per le più comuni operazioni IO ricco di filtri e configurazioni semplici da utilizzare.
Orientato al multithreading e alla gestione del codice a basso livello.

## Comandi supportati

* `find` - Cerca file nel file system
* `tree` - Mostra l'albero delle directory
* `eliminator` - Elimina file o cartelle in modo sicuro
* `count` - Conta il numero di file e/o cartelle
* `mdconverter` - Converte un file md in html (default) e pdf
* `grep` - Ricerca con espressioni regolari .NET (NonBacktracking, zero-alloc)
* `move` - Tool multithreaded per lo spostamento di file e cartelle

Per la guida completa di ogni comando usa il flag `--help` o `-h` (es. `swiss find --help`)

## Disclaimer e Responsabilità

Questo software è distribuito sotto licenza **MIT**. 
L'utilizzo dei comandi (in particolare quelli di eliminazione o modifica del file system come `eliminator` e `move`) è a totale discrezione e responsabilità dell'utente. Si consiglia sempre di testare i comandi in modalità simulata (`--debug` / `-d`) prima di eseguire operazioni distruttive.

## Licenza

Questo progetto è rilasciato sotto licenza [MIT](LICENSE).