import sqlite3

def init_db(db_path):
    conn = sqlite3.connect(db_path, check_same_thread=False)
    cursor = conn.cursor()
    cursor.execute('''
        CREATE TABLE IF NOT EXISTS log (
            epoch TEXT,
            offset INTEGER,
            content TEXT,
            PRIMARY KEY (epoch, offset)
        )
    ''')
    cursor.execute('DELETE FROM log')
    conn.commit()
    return conn

def insert_log(conn, epoch, offset, content):
    cursor = conn.cursor()
    cursor.execute(
        "INSERT OR IGNORE INTO log (epoch, offset, content) VALUES (?, ?, ?)",
        (epoch, offset, content)
    )
    conn.commit()

def fetch_log_entry(conn, offset):
    cursor = conn.cursor()
    cursor.execute("SELECT epoch, offset, content FROM log WHERE offset = ?", (offset,))
    return cursor.fetchone()