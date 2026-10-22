# M14 — fronteiras da compatibilidade de áudio

A BlitzCS continua compilador e infraestrutura genérica de símbolos externos.
`Blitz3DCS.Audio` pertence à integração de engine e não é referenciado pelo
compilador. O host opta pelo catálogo externo; chamadas emitidas continuam
estáticas. Inspecionar assinaturas não deve inicializar áudio.

## Três estágios

1. Resolução semântica: nome, parâmetros, conversões BASIC e retorno são
   verificados usando declarações tipadas. Esse estágio não comprova vínculo.
2. Vinculação/emissão: cada chamada externa precisa de um método gerenciado
   concreto e das bibliotecas distribuídas. Declaração sem implementação não
   produz silenciosamente um método vazio nem um assembly executável.
3. Execução: o método concreto abre o backend e pode falhar por biblioteca,
   driver ou dispositivo indisponível. Assembly emitido não comprova áudio nem
   execução do jogo.

## Escolha do backend inicial

| Alternativa | Vantagem | Custo/limite |
|---|---|---|
| FMOD 3.75 Win32 em worker x86 | Referência histórica executável | Proton/IPC, distribuição e licença separadas; não carrega em .NET Linux x64 |
| miniaudio com shim C estável | Dependência compacta, licença public domain/MIT-0 | Exige compilar/distribuir shim por plataforma e definir ABI |
| SDL3 Audio com binding privado | Runtime x64 já presente; API de dispositivos e lifetime explícito | Dependência nativa por plataforma; mixagem/streams FSOUND ainda precisam implementação |

Escolha da fatia M14: SDL3 para inicialização e fechamento reais, FMOD original
como referência isolada. Isso não escolhe renderer, mixer completo ou decoder.
A API de compatibilidade deve permitir trocar backend sem mudar o registro de
comandos do compilador. Não se reconhece toda API histórica como implementada.

As fontes primárias consultadas são a [API SDL_OpenAudioDevice](https://wiki.libsdl.org/SDL3/SDL_OpenAudioDevice),
a [migração SDL3](https://wiki.libsdl.org/SDL3/README-migration) e o
[projeto miniaudio](https://miniaud.io/). A migração documenta a inicialização
explícita de áudio e o refcount de InitSubSystem/QuitSubSystem. A licença SDL e
as dependências nativas devem ser consideradas ao distribuir seu binário.

## Recursos e segurança

Um endereço nativo nunca vira Integer BASIC. Tokens gerenciados positivos têm
invalidação explícita; não se reutiliza uma identidade obsoleta silenciosamente.
O owner libera cada recurso ao remover/fechar; referências de objetos BASIC e
seus handles existentes não são alterados pela tabela de recursos de áudio.

O perfil inicialmente comprovável é Init(44100,64,0)/Close. Flags e operações
não implementadas devem falhar explicitamente. Inicializar um driver dummy é
um teste de ABI/lifetime headless, não prova de reprodução física nem de
compatibilidade de streams com FMOD. A implementação e as medições finais são
registradas no relatório M14, separadas dessa decisão.
