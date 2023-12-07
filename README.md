# DV-WS-Bridge

dv-ws-bridgeはDynamicVariableをWebsocket経由で読み書きできるようにするためのModです。

## Usage

localhost:8787でlistenするのでそこに接続してください。

### 書き込み

syntax: `set SLOT_NAME/DV_NAME<TYPE> VALUE`

```
[totegamma@17:18]~$ wscat -c 172.29.240.1:8787
Connected (press CTRL+C to quit)
> set unique_name_slot/mybooldv<bool> true
> set unique_name_slot/myintdv<int> 57
> set unique_name_slot/myfloat3dv<float3> [1.0; 2.0; 3.0]
> set unique_name_slot/mystrdv<string> konnnichiwa
>
```

### 読み込み

syntax: `get SLOT_NAME/DV_NAME<TYPE>`

```
[totegamma@17:22]~$ wscat -c 172.29.240.1:8787
Connected (press CTRL+C to quit)
> get unique_name_slot/mybooldv<bool>
< VALUE unique_name_slot/mybooldv<bool> True
> get unique_name_slot/myintdv<int>
< VALUE unique_name_slot/myintdv<int> 57
> get unique_name_slot/myfloat3dv<float3>
< VALUE unique_name_slot/myfloat3dv<float3> [1; 2; 3]
> get unique_name_slot/mystrdv<string>
< VALUE unique_name_slot/mystrdv<string> konnnichiwa
>
```

## Notes

- 現在対応している型は、`string`, `bool`, `int`, `float`, `float2`, `float3`, `floatQ`です。
- Slot名が複数マッチした場合は動作が不定になります
- SlotをRootSlotからFindChildByNameを行った後、そのSlotに対してGetDynamicVariableSpaceを行うという処理になっています。なので、読み書きするDynamicVariableのコンポーネントと同じDV空間であれば、コマンドで指定する`SLOT_NAME`に直接DynamicValueVariableコンポーネントないしDynamicVariableSpaceがアタッチされている必要はありません。
- websocketでコマンドを送信する際、改行区切りで一度に複数のコマンドを送ることができます。

