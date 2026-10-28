# Roteiro da captura — ET-2517A (Brymen BM827s)

Teclas no terminal: **Enter** = confirma/captura · **n** = não bate · **r** = ler de novo · **s** = pular · **q** = terminar e salvar

"Pular" = etapa só do BM829s (o ET-2517A não tem): aperte **s**.
Botão "segurar" = manter apertado 1 s ou mais. "Toque" = aperto rápido.

## 1. Tensão DC (chave em V⎓, pontas em ΩV e COM)

| Etapa | O que fazer |
|---|---|
| dcv | Chave em V⎓, SELECT até sumir o visor pequeno, pontas abertas |
| dcv_short | Encoste uma ponta na outra |
| dcv_negative | Pontas invertidas numa pilha (vermelha no −, preta no +) |

## 2. Resistência (chave em Ω)

| Etapa | O que fazer |
|---|---|
| ohm_ol | Chave em Ω, pontas abertas, SELECT até mostrar só Ω (sem bip nem nS) — aparece 0L |
| ohm_body | Segure uma ponta em cada mão |
| ohm_short | Encoste uma ponta na outra |
| autocheck, autocheck_dc, autocheck_ac, autocheck_ohm | **Pular** (s) |

## 3. Tensão AC (chave em Hz V~)

| Etapa | O que fazer |
|---|---|
| acv | Chave em Hz V~, pontas abertas, SELECT até o grande mostrar ~ V e o pequeno Hz |
| dbm | **Pular** (s) |
| hz_acv | SELECT até o grande mostrar Hz e o pequeno V |

## 4. Funções na posição V⎓

| Etapa | O que fazer |
|---|---|
| dcv_acv | Chave em V⎓, SELECT até o grande mostrar ⎓ e o pequeno ~ V |
| acdcv | **Pular** (s) |
| ef | Segure HOLD (aparece E.F.), longe de fios |
| ncv | Aproxime o topo do medidor de um fio com tensão (sem encostar) — aparecem traços. Se não tiver, **s**. Depois segure HOLD para sair do EF |
| hold | SELECT até sumir o pequeno, toque HOLD (aparece H). Depois toque HOLD de novo |
| rel | Toque Δ (aparece Δ). Depois toque Δ de novo |
| manual_range | Toque RANGE (AUTO apaga) |
| range_auto | Segure RANGE (AUTO volta) |
| rec | Toque REC (aparece R e MAX MIN AVG) |
| rec_max | Toque REC (MAX) |
| rec_min | Toque REC (MIN) |
| rec_max_min | Toque REC (MAX-MIN) |
| rec_avg | Toque REC (AVG). Depois segure REC para sair |
| crest_max | Toque CREST (aparece C e MAX) |
| crest_min | Toque CREST (MIN) |
| crest_max_min | Toque CREST (MAX-MIN). Depois segure CREST para sair |

## 5. Milivolts

| Etapa | O que fazer |
|---|---|
| dcmv | Chave em mV⎓, pontas abertas, SELECT até sumir o pequeno |
| dcmv_acmv | SELECT até o grande ⎓ e o pequeno ~ mV |
| acdcmv | **Pular** (s) |
| logic_hz | SELECT até aparecer Hz |
| duty | SELECT até aparecer D% |
| acmv | Chave em Hz mV~, SELECT até o grande ~ mV e o pequeno Hz |
| dbm_mv | **Pular** (s) |
| hz_acmv | SELECT até o grande Hz e o pequeno mV |

## 6. Continuidade e nS

| Etapa | O que fazer |
|---|---|
| cont | Chave em Ω, SELECT até o símbolo de bip; encoste as pontas |
| ns | SELECT até aparecer nS, pontas abertas |

## 7. Temperatura (puladas nesta rodada — você respondeu "3")

Se aparecerem: t1_open, t1, temp_unit → **s**. t2, t1_t2, t1_minus_t2 → **s** (só BM829s).
Faremos t1_open, t1 e temp_unit depois, numa rodada separada com o termopar.

## 8. Capacitância e diodo

| Etapa | O que fazer |
|---|---|
| cap | Pontas em ΩV e COM, abertas; chave em capacitância, SELECT até sumir "diod". Pode demorar alguns segundos |
| diode | SELECT até aparecer "diod" no pequeno, pontas abertas |

## 9. Corrente — ponta vermelha em mA µA

| Etapa | O que fazer |
|---|---|
| dcua | Vermelha em mA µA, pontas abertas; chave em µA, SELECT até ⎓ sem o pequeno |
| acdcua | SELECT até ⎓ e ~ juntos. Se nunca aparecer, **s** |
| acua | SELECT até o grande ~ e o pequeno Hz |
| dcma | Chave em A mA, SELECT até ⎓ sem o pequeno |
| acdcma | SELECT até ⎓ e ~ juntos. Se nunca aparecer, **s** |
| acma | SELECT até o grande ~ e o pequeno Hz |

## 10. Corrente — ponta vermelha em A

| Etapa | O que fazer |
|---|---|
| dca | Vermelha em A, pontas abertas; chave em A mA, SELECT até ⎓ sem o pequeno |
| acdca | SELECT até ⎓ e ~ juntos. Se nunca aparecer, **s** |
| aca | SELECT até o grande ~ e o pequeno Hz |
| iner | Vermelha ainda em A, chave em V⎓ — o medidor apita e mostra InEr |

## 11. Final

| Etapa | O que fazer |
|---|---|
| extra | Livre: descreva algum modo não coberto, ou só Enter/s para pular |
| detect | Desligue e ligue o medidor quando ele pedir |

Ao terminar, volta o prompt `PS C:\...>`. Volte a ponta vermelha para ΩV e me avise.
