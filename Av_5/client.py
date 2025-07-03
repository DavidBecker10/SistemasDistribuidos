import grpc
import replicacao_dados_pb2 as pb2
import replicacao_dados_pb2_grpc as pb2_grpc

def menu():
    print("\nCliente:")
    print("1. Enviar dado")
    print("2. Consultar dado por offset")
    print("3. Sair")

def main():
    channel = grpc.insecure_channel("localhost:50051")
    stub = pb2_grpc.LeaderServiceStub(channel)

    while True:
        menu()
        op = input("Escolha: ").strip()
        if op == "1":
            content = input("Digite o conteúdo: ")
            data = pb2.Data(epoch="1", offset=0, content=content)
            resp = stub.ReceiveData(data)
            print(f"Resposta: {resp.message}")
        elif op == "2":
            try:
                offset = int(input("Offset: "))
                req = pb2.QueryRequest(offset=offset)
                res = stub.QueryData(req)
                if res.offset == -1:
                    print("Não encontrado.")
                else:
                    print(f"Epoch: {res.epoch}, Offset: {res.offset}, Conteúdo: {res.content}")
            except:
                print("Offset inválido.")
        elif op == "3":
            break
        else:
            print("Opção inválida.")

if __name__ == "__main__":
    main()