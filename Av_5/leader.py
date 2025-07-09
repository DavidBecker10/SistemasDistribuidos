import grpc
from concurrent import futures
import replicacao_dados_pb2 as pb2
import replicacao_dados_pb2_grpc as pb2_grpc
from threading import Lock
from database import init_db, insert_log, fetch_log_entry

class LeaderService(pb2_grpc.LeaderServiceServicer):
    def __init__(self):
        self.epoch = "1"
        self.lock = Lock()
        self.log = []
        self.replicas = ["localhost:50052", "localhost:50053", "localhost:50054"]
        self.db_intermediario = init_db("leader_intermediario.db")
        self.db_final = init_db("leader_final.db")

    def ReceiveData(self, request, context):
        with self.lock:
            offset = len(self.log)
            data = pb2.Data(epoch=self.epoch, offset=offset, content=request.content)
            self.log.append(data)
            insert_log(self.db_intermediario, data.epoch, data.offset, data.content)
            print(f"[LÍDER] Recebido do cliente: {data}")

        ack_count = self.propagate_to_replicas(data)
        if ack_count >= len(self.replicas) // 2 + 1:
            self.commit_to_replicas(data)
            insert_log(self.db_final, data.epoch, data.offset, data.content)
            return pb2.Ack(message="Gravação confirmada com commit.")
        else:
            return pb2.Ack(message="Falha no quórum.")

    def propagate_to_replicas(self, data):
        ack_count = 0
        for addr in self.replicas:
            try:
                channel = grpc.insecure_channel(addr)
                stub = pb2_grpc.ReplicaServiceStub(channel)
                resp = stub.ReceiveDataFromLeader(data)
                if resp.message == "ACK":
                    ack_count += 1
            except Exception as e:
                print(f"[LÍDER] Falha ao propagar para {addr}: {e}")
        return ack_count

    def commit_to_replicas(self, data):
        order = pb2.CommitOrder(epoch=data.epoch, offset=data.offset)
        for addr in self.replicas:
            try:
                channel = grpc.insecure_channel(addr)
                stub = pb2_grpc.ReplicaServiceStub(channel)
                stub.CommitData(order)
            except Exception as e:
                print(f"[LÍDER] Falha ao enviar commit a {addr}: {e}")

    def QueryData(self, request, context):
        result = fetch_log_entry(self.db_final, request.offset)
        if result:
            epoch, offset, content = result
            return pb2.QueryResponse(epoch=epoch, offset=offset, content=content)
        else:
            return pb2.QueryResponse(epoch="", offset=-1, content="Não encontrado")
        
    def SyncLog(self, request, context):
        cursor = self.db_final.cursor()
        cursor.execute("SELECT epoch, offset, content FROM log WHERE epoch = ? AND offset >= ? ORDER BY offset",
                    (request.epoch, request.offset))
        entries = cursor.fetchall()
        return pb2.SyncLogResponse(
            entries=[pb2.Data(epoch=e[0], offset=e[1], content=e[2]) for e in entries]
        )

def serve():
    server = grpc.server(futures.ThreadPoolExecutor(max_workers=10))
    pb2_grpc.add_LeaderServiceServicer_to_server(LeaderService(), server)
    server.add_insecure_port("[::]:50051")
    print("Líder ativo na porta 50051...")
    server.start()
    server.wait_for_termination()

if __name__ == "__main__":
    serve()