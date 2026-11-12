# Idle Senior Supremacy (ISS)

> A modern Steam games viewer and manager for card farming, playtime idling, and achievement editing.

**Idle Senior Supremacy (ISS)** é um aplicativo desktop para Windows construído em **.NET 9** e **WPF UI (Fluent Design)**, reunindo em uma única interface moderna o gerenciamento completo da sua conta Steam:

---

## ✨ Funcionalidades

- **Dashboard & Analytics da Conta:**
  - Nível Steam, barra de XP real, insígnias e status de segurança (VAC, Trade Ban, Game Bans).
  - Diagnóstico da biblioteca (jogos nunca jogados, `< 5h`, `5h+`, horas totais).
  - Valor da biblioteca em tempo real (**Preços de Hoje**, **Valor Cheio**, **Preço Médio por Jogo**, **Preço Médio por Hora**) com consulta em lote na Steam Store.
  - Sugestão aleatória de jogo ("O que jogar hoje?") e últimas notícias dos seus jogos.
- **Farm de Cartas (Card Farm):**
  - Modo simultâneo (lotes de até 25 jogos `< 2h`) e modo sequencial, com filtro de horas mínimas, lista de bloqueio e verificação automática de drops restantes.
- **Farm de Horas (Hours Farm):**
  - Execução simultânea (até 32 jogos ativos na Steam) ou sequencial (`1 por vez`).
  - **Fila de Farm de Horas** dedicada com tempo decorrido ao vivo, meta configurável, reordenação de prioridade (`▲ / ▼`), **Iniciar Todos**, **Parar Todos** e adição em lote.
- **Biblioteca & Painel do Jogo:**
  - Pesquisa, filtros, banners em alta definição, preço atual na loja com badge de desconto (`-XX%`), botão **Ver na Steam** (`steam://store/...`), estimativas de tempo (HLTB / SteamHunters) e alertas de Anti-Cheat.
- **Gerenciador Orgânico de Conquistas:**
  - Desbloqueio/bloqueio de conquistas com agendamento orgânico e proteção contra alterações acidentais.
- **Personalização Completa:**
  - **Idiomas:** Português (Brasil) e English (US).
  - **Moedas nativas da Steam:** `BRL (R$)`, `USD ($)`, `EUR (€)` e `GBP (£)` sem conversão artificial.
  - **Temas:** `Dark`, `Light`, `Spring`, `Dracula` e `Midnight`.

---

## 📦 Como Usar (Usuário Final)

1. Baixe a versão mais recente na aba [**Releases**](https://github.com/JiukOn/Idle-Senior-Supremacy-ISS-/releases).
2. Extraia o arquivo `.zip` em uma pasta de sua preferência.
3. Certifique-se de que o **Steam Desktop Client** está aberto e logado na sua conta.
4. Execute `IdleSenior.UI.exe`.

> **Aviso sobre o Windows SmartScreen:** Como o projeto é distribuído gratuitamente sem certificado de assinatura digital pago, o Windows pode exibir um alerta na primeira execução. Clique em *"Mais informações"* → *"Executar assim mesmo"*.

---

## 🛠️ Como Compilar (Desenvolvedores)

### Pré-requisitos
- [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- Windows 10/11

### Build & Empacotamento Automático
Execute o script na raiz do repositório:

```bat
build.bat
```

O script executa:
1. `dotnet build` em modo `Release` (validando 0 avisos e 0 erros);
2. `dotnet publish` *self-contained* (`win-x86`) para `publish\IdleSeniorSupremacy-win-x86\`;
3. Cópia dos workers (`IdleSenior.Worker.exe`, `IdleSenior.AchievementWorker.exe`) e `steam_api.dll`;
4. Geração automática do pacote `.zip` pronto para distribuição em `publish\`.

---

## 📄 Licença

Copyright © 2026 **JiukOn**. Todos os direitos reservados.  
Consulte o arquivo [`LICENSE`](LICENSE) para os termos completos de uso.
