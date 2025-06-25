from sqlalchemy.ext.asyncio import AsyncSession, create_async_engine
from sqlalchemy.orm import sessionmaker, declarative_base
from sqlalchemy import Column, Integer, String, Float, Text, Boolean

DATABASE_URL = "sqlite+aiosqlite:///./rmgt.db"
engine = create_async_engine(DATABASE_URL, echo=True, future=True)
SessionLocal = sessionmaker(engine, expire_on_commit=False, class_=AsyncSession)
Base = declarative_base()

class ForSaleItem(Base):
    __tablename__ = "for_sale_items"
    id = Column(Integer, primary_key=True, index=True)
    def_name = Column(String, index=True)
    quantity = Column(Integer)
    price = Column(Integer)
    player_name = Column(String)
    seller_steam_id = Column(String, index=True)
    quality = Column(String, default="")
    item_id = Column(String, nullable=True)
    listed_at = Column(Float)

class SaleHistory(Base):
    __tablename__ = "sales_history"
    id = Column(Integer, primary_key=True, index=True)
    timestamp = Column(Float)
    buyer_steam_id = Column(String, index=True)
    buyer_name = Column(String)
    purchased_items = Column(Text)  # JSON string
    total_cost = Column(Integer)
    seller_steam_id = Column(String, index=True, nullable=True)
    seller_name = Column(String, nullable=True)

class PendingSale(Base):
    __tablename__ = "pending_sales"
    id = Column(Integer, primary_key=True, index=True)
    seller_steam_id = Column(String, index=True)
    buyer_name = Column(String)
    item = Column(String)
    quantity = Column(Integer)
    price = Column(Integer)
    total_silver = Column(Integer)
    timestamp = Column(Float)

class ActiveUser(Base):
    __tablename__ = "active_users"
    steam_id = Column(String, primary_key=True, index=True)
    player_name = Column(String)
    last_seen = Column(Float)

class ActiveToken(Base):
    __tablename__ = "active_tokens"
    token = Column(String, primary_key=True, index=True)
    steam_id = Column(String, index=True)
    player_name = Column(String)
    issued_at = Column(Float)
    expires_at = Column(Float)
    revoked = Column(Integer, default=0)

class UserSession(Base):
    __tablename__ = "user_sessions"
    id = Column(Integer, primary_key=True, index=True)
    steam_id = Column(String, index=True)
    player_name = Column(String)
    session_start = Column(Float)
    session_end = Column(Float, nullable=True)  # NULL if session is still active
    last_activity = Column(Float)
    is_active = Column(Boolean, default=True)
    user_agent = Column(String, nullable=True)

async def init_db():
    async with engine.begin() as conn:
        await conn.run_sync(Base.metadata.create_all) 